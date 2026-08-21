// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using AppRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        public async Task<DesktopUpdateStatusSnapshot> CheckForUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Manual,
            CancellationToken cancellationToken = default)
        {
            string sourceName = FormatUpdateCheckSource(source);
            try
            {
                Trace.TraceInformation("Starting desktop update check: source={0}, currentVersion={1}.", sourceName, DesktopAppVersion.Current);
                DesktopUpdateCheckResult check = await _updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
                RecordUpdateCheckSuccess(sourceName, check, installerPath: null);
                Trace.TraceInformation(
                    "Desktop update check completed: source={0}, currentVersion={1}, latestVersion={2}, updateAvailable={3}, installerAsset={4}.",
                    sourceName,
                    check.CurrentVersion,
                    check.LatestVersion,
                    check.IsUpdateAvailable,
                    check.InstallerAsset?.Name ?? "none");
                return ToUpdateStatus(check, installerPath: null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordUpdateCheckFailure(sourceName, exception);
                Trace.TraceWarning("Desktop update check failed: source={0}, error={1}.", sourceName, exception);
                throw;
            }
        }

        public async Task<DesktopUpdateStatusSnapshot> DownloadUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Download,
            IProgress<DesktopUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string sourceName = FormatUpdateCheckSource(source);
            try
            {
                Trace.TraceInformation("Starting desktop update download flow: source={0}, currentVersion={1}.", sourceName, DesktopAppVersion.Current);
                DesktopUpdateCheckResult check = await _updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
                if (!check.IsUpdateAvailable || check.InstallerAsset is null)
                {
                    RecordUpdateCheckSuccess(sourceName, check, installerPath: null);
                    return ToUpdateStatus(check, installerPath: null);
                }

                DesktopUpdateDownloadResult download = await _updateService
                    .DownloadInstallerAsync(check, progress, cancellationToken)
                    .ConfigureAwait(false);
                new DesktopPendingUpdateStore(_paths.UpdateCacheDirectory).Save(new DesktopPendingUpdate(
                    check.LatestVersion.ToString(),
                    download.FilePath,
                    download.Sha256,
                    download.SizeBytes,
                    DateTime.UtcNow));
                RecordUpdateCheckSuccess(sourceName, check, download.FilePath);
                Trace.TraceInformation(
                    "Desktop update download completed: source={0}, latestVersion={1}, installerAsset={2}, sizeBytes={3}.",
                    sourceName,
                    check.LatestVersion,
                    download.InstallerAsset.Name,
                    download.SizeBytes);
                return ToUpdateStatus(check, download.FilePath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordUpdateCheckFailure(sourceName, exception);
                Trace.TraceWarning("Desktop update download flow failed: source={0}, error={1}.", sourceName, exception);
                throw;
            }
        }

        public Task<DesktopUpdateInstallResult> InstallDownloadedUpdateAsync(
            string installerPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.TraceInformation("Starting desktop update installer launch.");
            try
            {
                DesktopUpdateInstallResult result = _updateInstaller.StartSilentInstall(installerPath, launchAfterUpdate: true);
                _lastUpdateDiagnostics = _lastUpdateDiagnostics.WithInstallLaunch(result, DateTimeOffset.UtcNow);
                Trace.TraceInformation(
                    "Desktop update installer launch completed: processId={0}, exitedDuringStartupProbe={1}, exitCode={2}.",
                    result.ProcessId,
                    result.ExitedDuringStartupProbe,
                    result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
                return Task.FromResult(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _lastUpdateDiagnostics = _lastUpdateDiagnostics.WithInstallLaunchFailure(exception, DateTimeOffset.UtcNow);
                Trace.TraceWarning("Desktop update installer launch failed: error={0}.", exception);
                throw;
            }
        }

        private void RecordUpdateCheckSuccess(
            string source,
            DesktopUpdateCheckResult check,
            string? installerPath)
        {
            _lastUpdateDiagnostics = DesktopUpdateDiagnosticsSnapshot.FromCheck(
                source,
                check,
                installerPath,
                DateTimeOffset.UtcNow);
        }

        private void RecordUpdateCheckFailure(
            string source,
            Exception exception)
        {
            _lastUpdateDiagnostics = DesktopUpdateDiagnosticsSnapshot.FromFailure(
                source,
                DesktopAppVersion.Current,
                exception,
                DateTimeOffset.UtcNow);
        }

        private static DesktopUpdateStatusSnapshot ToUpdateStatus(
            DesktopUpdateCheckResult check,
            string? installerPath)
        {
            string current = check.CurrentVersion.ToString();
            string latest = check.LatestVersion.ToString();
            bool installerReady = !string.IsNullOrWhiteSpace(installerPath);
            string details;
            if (!check.IsUpdateAvailable)
            {
                details = "Cotton Sync is up to date.";
            }
            else if (installerReady)
            {
                details = "Update " + latest
                    + " is ready. Click Update to install it now, or it will install automatically on next app start.";
            }
            else if (check.InstallerAsset is null)
            {
                details = "Update " + latest + " is available, but no Windows installer asset was found.";
            }
            else
            {
                details = "Update " + latest + " is available.";
            }

            return new DesktopUpdateStatusSnapshot(
                current,
                latest,
                check.IsUpdateAvailable,
                installerReady,
                details,
                installerPath,
                check.Manifest.ReleaseUrl);
        }

        private static string FormatUpdateCheckSource(DesktopUpdateCheckSource source)
        {
            return source switch
            {
                DesktopUpdateCheckSource.Manual => "manual",
                DesktopUpdateCheckSource.Periodic => "periodic",
                DesktopUpdateCheckSource.Startup => "startup",
                DesktopUpdateCheckSource.Download => "download",
                _ => "unknown",
            };
        }
    }
}
