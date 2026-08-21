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
        public async Task<DesktopShellSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await InitializeSyncStateStoreAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            bool startWithOperatingSystem = await ResolveStartWithOperatingSystemAsync(
                preferences,
                cancellationToken).ConfigureAwait(false);
            bool appliedAutostart = await TryApplyPreferredAutostartAsync(
                preferences,
                cancellationToken).ConfigureAwait(false);
            if (appliedAutostart)
            {
                startWithOperatingSystem = true;
                await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
            ReplaceKnownSyncPairSettings(syncPairs);
            Uri? serverUrl = _startupOptions.ServerUrl ?? preferences.RememberedServerUrl;
            DesktopStoredSessionRestoreSnapshot sessionRestore = new(null, false, null);
            if (serverUrl is not null)
            {
                sessionRestore = await TryRestoreSessionAsync(serverUrl, cancellationToken).ConfigureAwait(false);
            }

            AuthSession? session = sessionRestore.Session;
            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            IReadOnlyList<DesktopSyncPairSnapshot> syncPairSnapshots = await BuildSyncPairSnapshotsAsync(
                syncPairs,
                cancellationToken).ConfigureAwait(false);
            return new DesktopShellSnapshot(
                serverUrl,
                session?.Email ?? session?.Username,
                _startupOptions.Username ?? preferences.RememberedUsername,
                startWithOperatingSystem,
                preferences.EnableNotifications,
                preferences.ThemeMode,
                CreateDataPathSnapshot(),
                platformCapabilities with { IsAutostartSupported = _autostartService.IsSupported },
                session is not null,
                syncPairSnapshots,
                DesktopDeviceIdentity.CreateDeviceName(),
                sessionRestore.ErrorMessage,
                sessionRestore.HasStoredSession);
        }

        public void Dispose()
        {
            DesktopSyncApplicationHost? host = DetachHost();
            if (host is not null)
            {
                StopAndDisposeHostAsync(host).GetAwaiter().GetResult();
            }

            _updateServiceLifetime?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            DesktopSyncApplicationHost? host = DetachHost();
            if (host is not null)
            {
                await StopAndDisposeHostAsync(host).ConfigureAwait(false);
            }

            _updateServiceLifetime?.Dispose();
        }

        public static DesktopShellController CreateDefault(DesktopStartupOptions? startupOptions = null)
        {
            return CreateDefault(DesktopAppPaths.CreateDefault(), startupOptions);
        }

        public static DesktopShellController CreateDefault(
            DesktopAppPaths paths,
            DesktopStartupOptions? startupOptions = null)
        {
            ArgumentNullException.ThrowIfNull(paths);
            var loggerFactory = new DesktopTraceLoggerFactory();
            return new DesktopShellController(
                paths,
                new DesktopSyncApplicationFactory(paths, loggerFactory),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                new ProcessPlatformCommandService(loggerFactory.CreateLogger<ProcessPlatformCommandService>()),
                DesktopAutostartServiceFactory.CreateDefault(),
                new DesktopShellControllerOptions
                {
                    StartupOptions = startupOptions ?? DesktopStartupOptions.Empty,
                });
        }
    }
}
