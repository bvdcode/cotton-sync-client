// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private async Task CheckForUpdatesAsync()
        {
            await RunUpdateActionAsync(
                "Checking for updates",
                () => _controller.CheckForUpdateAsync()).ConfigureAwait(true);
        }

        private async Task DownloadUpdateAsync()
        {
            var progress = new ActionProgress<DesktopUpdateDownloadProgress>(ApplyUpdateDownloadProgress);
            ShowPreparingUpdateDownloadProgress();
            UpdateDetailsText = "Preparing update download.";
            await RunUpdateActionAsync(
                "Downloading update",
                () => _controller.DownloadUpdateAsync(DesktopUpdateCheckSource.Download, progress),
                updateGlobalStatusOnStart: true).ConfigureAwait(true);
        }

        private void ApplyUpdateDownloadProgress(DesktopUpdateDownloadProgress progress)
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.Post(() => ApplyUpdateDownloadProgress(progress));
                return;
            }

            UpdateStatusText = "Downloading update";
            UpdateDetailsText = FormatUpdateDownloadProgress(progress);
            GlobalStatus = "Downloading update";
            IsUpdateDownloadProgressVisible = true;
            if (progress.TotalBytes is > 0)
            {
                IsUpdateDownloadProgressIndeterminate = false;
                UpdateDownloadProgressValue = Math.Clamp(
                    progress.BytesDownloaded / (double)progress.TotalBytes.Value * 100d,
                    0d,
                    100d);
            }
            else
            {
                IsUpdateDownloadProgressIndeterminate = true;
                UpdateDownloadProgressValue = 0d;
            }

            OnPropertyChanged(nameof(HasUpdateDetails));
        }

        private void ShowPreparingUpdateDownloadProgress()
        {
            IsUpdateDownloadProgressVisible = true;
            IsUpdateDownloadProgressIndeterminate = true;
            UpdateDownloadProgressValue = 0d;
        }

        private void ClearUpdateDownloadProgress()
        {
            IsUpdateDownloadProgressVisible = false;
            IsUpdateDownloadProgressIndeterminate = false;
            UpdateDownloadProgressValue = 0d;
        }

        private void BeginStartupUpdateCheck()
        {
            if (!_checkForUpdatesOnStartup)
            {
                return;
            }

            _startupUpdateCancellation?.Cancel();
            _startupUpdateCancellation?.Dispose();
            _startupUpdateCancellation = new CancellationTokenSource();
            _startupUpdateTask = RunStartupUpdateCheckAsync(_startupUpdateCancellation.Token);
        }

        private async Task RunStartupUpdateCheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var progress = new ActionProgress<DesktopUpdateDownloadProgress>(ApplyUpdateDownloadProgress);
                await RunUpdateActionAsync(
                        "Checking for updates",
                        () => _controller.DownloadUpdateAsync(
                            DesktopUpdateCheckSource.Startup,
                            progress,
                            cancellationToken: cancellationToken),
                        updateGlobalStatusOnFailure: false,
                        notifyWhenInstallerReady: true)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BeginPeriodicUpdateChecks();
                }
            }
        }

        private void BeginPeriodicUpdateChecks()
        {
            _periodicUpdateCancellation?.Cancel();
            _periodicUpdateCancellation?.Dispose();
            _periodicUpdateCancellation = new CancellationTokenSource();
            _periodicUpdateTask = RunPeriodicUpdateChecksAsync(_periodicUpdateCancellation.Token);
        }

        private async Task RunPeriodicUpdateChecksAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _updateDelayAsync(_periodicUpdateCheckInterval, cancellationToken).ConfigureAwait(true);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (IsUpdateBusy || IsUpdateReady)
                    {
                        continue;
                    }

                    await RunUpdateActionAsync(
                            "Checking for updates",
                            () => _controller.CheckForUpdateAsync(DesktopUpdateCheckSource.Periodic, cancellationToken),
                            updateGlobalStatusOnFailure: false)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task InstallUpdateAsync()
        {
            string installerPath = _downloadedUpdateInstallerPath;
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                throw new InvalidOperationException("No downloaded Cotton Sync update is ready to install.");
            }

            IsUpdateBusy = true;
            UpdateStatusText = "Installing update";
            UpdateDetailsText = "Starting the update installer.";
            ClearUpdateDownloadProgress();
            IsUpdateInstallProgressVisible = true;
            GlobalStatus = "Installing update";
            try
            {
                DesktopUpdateInstallResult result =
                    await _controller.InstallDownloadedUpdateAsync(installerPath).ConfigureAwait(true);
                IsUpdateInstallHandoffActive = true;
                UpdateStatusText = "Installing update";
                UpdateDetailsText = result.ExitedDuringStartupProbe
                    ? "Update installer launched and handed off to Windows. Cotton Sync will restart after the update is installed."
                    : "Update installer launched. Cotton Sync will restart after the update is installed.";
                GlobalStatus = "Installing update";
                AddActivity("Update", string.Empty, "Silent update installer started");
                UpdateInstallShutdownRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string message = ResolveUpdateFailureMessage(exception);
                IsUpdateInstallHandoffActive = false;
                IsUpdateInstallProgressVisible = false;
                UpdateStatusText = "Update failed";
                UpdateDetailsText = message;
                GlobalStatus = "Update failed";
                AddActivity("Warning", "Update", message);
            }
            finally
            {
                IsUpdateBusy = false;
            }
        }

        private async Task RunUpdateActionAsync(
            string busyStatus,
            Func<Task<DesktopUpdateStatusSnapshot>> updateActionAsync,
            bool updateGlobalStatusOnStart = false,
            bool updateGlobalStatusOnFailure = true,
            bool notifyWhenInstallerReady = false)
        {
            string previousGlobalStatus = GlobalStatus;
            IsUpdateBusy = true;
            IsUpdateInstallHandoffActive = false;
            UpdateStatusText = busyStatus;
            if (updateGlobalStatusOnStart)
            {
                GlobalStatus = busyStatus;
            }

            try
            {
                DesktopUpdateStatusSnapshot result = await updateActionAsync().ConfigureAwait(true);
                bool updateOwnedGlobalStatus = updateGlobalStatusOnStart || IsUpdateOperationGlobalStatus(GlobalStatus);
                ApplyUpdateStatus(result);
                if (updateOwnedGlobalStatus)
                {
                    GlobalStatus = result.IsUpdateAvailable
                        ? UpdateStatusText
                        : previousGlobalStatus;
                }

                AddActivity("Update", result.ReleaseUrl?.AbsoluteUri ?? string.Empty, result.Details);
                if (notifyWhenInstallerReady && result.IsInstallerReady)
                {
                    ShowNativeNotification("Update ready", "Cotton Sync will install this update on next app start.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string message = ResolveUpdateFailureMessage(exception);
                UpdateStatusText = "Update failed";
                UpdateDetailsText = message;
                ClearUpdateDownloadProgress();
                if (updateGlobalStatusOnFailure)
                {
                    GlobalStatus = "Update failed";
                }
                else
                {
                    GlobalStatus = previousGlobalStatus;
                }

                AddActivity("Warning", "Update", message);
            }
            finally
            {
                IsUpdateBusy = false;
            }
        }

        private void ApplyUpdateStatus(DesktopUpdateStatusSnapshot status)
        {
            _downloadedUpdateInstallerPath = status.InstallerPath ?? string.Empty;
            IsUpdateInstallHandoffActive = false;
            IsUpdateAvailable = status.IsUpdateAvailable;
            IsUpdateReady = status.IsInstallerReady;
            IsUpdateInstallProgressVisible = false;
            ClearUpdateDownloadProgress();
            UpdateStatusText = status.IsInstallerReady
                ? "Update ready"
                : status.IsUpdateAvailable ? "Update available" : "Up to date";
            UpdateDetailsText = status.Details;
        }

        private static bool IsUpdateOperationGlobalStatus(string status)
        {
            return string.Equals(status, "Checking for updates", StringComparison.Ordinal)
                || string.Equals(status, "Downloading update", StringComparison.Ordinal);
        }

        private static string ResolveUpdateFailureMessage(Exception exception)
        {
            if (exception is HttpRequestException httpException)
            {
                if (httpException.StatusCode == HttpStatusCode.NotFound)
                {
                    return "Update metadata or installer was not found. Retry after the release finishes publishing.";
                }

                if (httpException.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return "GitHub is rate limiting update checks. Wait a moment and retry.";
                }

                if (httpException.StatusCode.HasValue && (int)httpException.StatusCode.Value >= 500)
                {
                    return "GitHub release server is unavailable. Retry later.";
                }

                return "Cannot reach update server. Check network or firewall and retry.";
            }

            if (exception is TaskCanceledException or TimeoutException)
            {
                return "Update check timed out. Check network or firewall and retry.";
            }

            if (exception is InvalidDataException
                && exception.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase))
            {
                return "Downloaded update failed integrity verification. Delete the cached update and retry download.";
            }

            if (exception is InvalidDataException
                && exception.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase))
            {
                return "Release manifest is invalid. Retry after the release finishes publishing.";
            }

            return DesktopActionRequiredMessageResolver.FromException(exception);
        }
    }
}
