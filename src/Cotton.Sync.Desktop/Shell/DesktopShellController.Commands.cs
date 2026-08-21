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
        public Task SyncAllAsync(
            CancellationToken cancellationToken = default,
            Guid? syncPairId = null,
            RemoteDeletePlanApproval? approvedRemoteDeletePlan = null)
        {
            if (!syncPairId.HasValue && approvedRemoteDeletePlan is not null)
            {
                throw new ArgumentException(
                    "A sync pair is required when approving a remote delete plan.",
                    nameof(syncPairId));
            }

            if (syncPairId.HasValue)
            {
                SyncRunRequest request = SyncRunRequest.ForFull(
                    SyncRunCause.Manual,
                    approvedRemoteDeletePlan);
                return RequireHost().App.SyncNowAsync(syncPairId.Value, request, cancellationToken);
            }

            return RequireHost().App.SyncAllAsync(cancellationToken);
        }

        public Task PauseAllAsync(CancellationToken cancellationToken = default)
        {
            return RequireHost().App.PauseAllAsync(cancellationToken);
        }

        public Task ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            return RequireHost().App.ResumeAllAsync(cancellationToken);
        }

        public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
        {
            return _platformCommands.OpenFolderAsync(localPath, cancellationToken);
        }

        public async Task OpenWebAsync(CancellationToken cancellationToken = default)
        {
            Uri? serverUrl = _host?.ServerUrl;
            if (serverUrl is null)
            {
                await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
                serverUrl = _startupOptions.ServerUrl ?? preferences.RememberedServerUrl;
            }

            if (serverUrl is null)
            {
                throw new InvalidOperationException("Sign in before opening Cotton Cloud.");
            }

            await _platformCommands.OpenWebAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetStartWithOperatingSystemAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (enabled && !_autostartService.IsSupported)
            {
                throw new NotSupportedException("Autostart is not supported on this platform yet.");
            }

            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _autostartService.SetEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.StartWithOperatingSystem = enabled;
            preferences.StartMinimizedToTray = enabled && DesktopPlatformCapabilities.IsTrayLifecycleSupported;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> ResolveStartWithOperatingSystemAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken)
        {
            bool isEnabled = await TryReadStartWithOperatingSystemAsync(cancellationToken).ConfigureAwait(false);
            if (isEnabled)
            {
                return true;
            }

            return preferences.StartWithOperatingSystem && _autostartService.IsSupported;
        }

        private async Task<bool> TryReadStartWithOperatingSystemAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceError("Failed to read Cotton Sync autostart state. {0}", exception);
                return false;
            }
        }

        private async Task<bool> TryApplyPreferredAutostartAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken)
        {
            if (!preferences.StartWithOperatingSystem || !_autostartService.IsSupported)
            {
                return false;
            }

            try
            {
                bool isEnabled = await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
                if (!isEnabled)
                {
                    await _autostartService.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                }

                preferences.StartMinimizedToTray = DesktopPlatformCapabilities.IsTrayLifecycleSupported;
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceError("Failed to apply Cotton Sync autostart preference. {0}", exception);
                return false;
            }
        }

        public async Task SetNotificationsEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.EnableNotifications = enabled;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetThemeModeAsync(AppThemeMode themeMode, CancellationToken cancellationToken = default)
        {
            ValidateThemeMode(themeMode);
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.ThemeMode = themeMode;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        private static void ValidateThemeMode(AppThemeMode themeMode)
        {
            if (!Enum.IsDefined(themeMode))
            {
                throw new ArgumentOutOfRangeException(nameof(themeMode), themeMode, "Unsupported desktop theme mode.");
            }
        }
    }
}
