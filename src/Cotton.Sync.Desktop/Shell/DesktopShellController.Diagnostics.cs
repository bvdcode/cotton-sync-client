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
        public async Task<string> ExportDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            return await ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> ExportDiagnosticsAsync(
            DesktopDiagnosticsExportOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
            SyncStateStoreDiagnostics syncStateDiagnostics = await CreateSyncStateDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            DesktopCloudFilesRegistrationDiagnosticsSnapshot cloudFilesRegistration =
                DesktopCloudFilesRegistrationDiagnosticsSnapshot.Create(syncPairs);
            DesktopNotificationCapabilitySnapshot notificationCapabilities =
                DesktopNotificationServiceFactory.CreateSelfTestCapabilitySnapshot();
            IReadOnlyList<DesktopTransferProgressSnapshot> currentTransfers = GetCurrentTransfers();
            IReadOnlyList<DesktopRunProgressSnapshot> aggregateRunProgress = GetAggregateRunProgress();
            IReadOnlyList<DesktopSelfTestItemSnapshot> diagnosticsItems =
                await CreateDiagnosticsExportItemsAsync(
                    syncPairs,
                    syncStateDiagnostics,
                    cloudFilesRegistration,
                    notificationCapabilities,
                    cancellationToken).ConfigureAwait(false);
            var bundle = new DesktopDiagnosticsBundle(
                DateTimeOffset.UtcNow,
                DesktopAppVersion.Current,
                (_startupOptions.ServerUrl ?? preferences.RememberedServerUrl)?.AbsoluteUri,
                _host is null ? "Signed out" : preferences.RememberedUsername ?? "Signed in",
                CreateDataPathSnapshot(),
                await BuildSyncPairSnapshotsAsync(syncPairs, cancellationToken).ConfigureAwait(false),
                syncStateDiagnostics,
                CreateRuntimeHealthSnapshot(),
                CreateSyncLifecycleDiagnosticsSnapshot(syncPairs),
                CreateAuthDiagnosticsSnapshot(),
                DesktopNotificationDiagnosticsSnapshot.FromCapability(notificationCapabilities),
                CreateUpdateDiagnosticsSnapshot(),
                cloudFilesRegistration,
                diagnosticsItems,
                WindowsCloudFilesDiagnostics.Shared.Snapshot(),
                currentTransfers,
                aggregateRunProgress);
            return await _diagnosticsExporter.ExportAsync(_paths, bundle, options, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<DesktopSelfTestItemSnapshot>> CreateDiagnosticsExportItemsAsync(
            IReadOnlyList<SyncPairSettings> syncPairs,
            SyncStateStoreDiagnostics syncStateDiagnostics,
            DesktopCloudFilesRegistrationDiagnosticsSnapshot cloudFilesRegistration,
            DesktopNotificationCapabilitySnapshot notificationCapabilities,
            CancellationToken cancellationToken)
        {
            var items = new List<DesktopSelfTestItemSnapshot>
            {
                new(
                    "Diagnostics export",
                    true,
                    "Captured current diagnostics and read-only capability checks; self-test probes were not run."),
                new(
                    "Sync pair database",
                    true,
                    syncPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sync pair(s)"),
                CreateSyncStateDiagnosticsItem(syncStateDiagnostics),
            };

            await AddSelfTestCheckAsync(
                items,
                "Authentication state",
                () => CheckAuthenticationStateAsync(cancellationToken)).ConfigureAwait(false);

            DesktopTokenStorageCapabilitySnapshot tokenStorage = DesktopTokenStorageCapabilities.CreateSnapshot();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Token storage",
                tokenStorage.IsReleaseSecure,
                tokenStorage.IsReleaseSecure
                    ? tokenStorage.Details
                    : tokenStorage.Details + " (not release secure)"));

            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Desktop platform",
                true,
                platformCapabilities.OperatingSystemName
                    + "; session: "
                    + platformCapabilities.DesktopSession
                    + "; desktop: "
                    + platformCapabilities.CurrentDesktop));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Tray lifecycle",
                true,
                platformCapabilities.TrayLifecycleDetails));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Windows virtual files",
                platformCapabilities.IsWindowsVirtualFilesSupported,
                "Read-only capability check: "
                    + platformCapabilities.WindowsVirtualFilesDetails
                    + " Full Cloud Files connection self-test was not run during diagnostics export.",
                Skipped: !platformCapabilities.IsWindowsVirtualFilesSupported));
            items.Add(CreateNotificationSelfTestItem(notificationCapabilities));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Cloud Files registration",
                cloudFilesRegistration.MissingSyncPairCount == 0 && cloudFilesRegistration.UnknownSyncPairCount == 0,
                CreateCloudFilesRegistrationDetails(cloudFilesRegistration),
                Skipped: cloudFilesRegistration.VirtualFilesSyncPairCount == 0));

            return items;
        }

        private async Task InitializeSyncStateStoreAsync(CancellationToken cancellationToken)
        {
            var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<SyncStateStoreDiagnostics> CreateSyncStateDiagnosticsAsync(CancellationToken cancellationToken)
        {
            var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await stateStore.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }

        private DesktopUpdateDiagnosticsSnapshot CreateUpdateDiagnosticsSnapshot()
        {
            DesktopPendingUpdate? pendingUpdate =
                new DesktopPendingUpdateStore(_paths.UpdateCacheDirectory).TryLoad();
            return _lastUpdateDiagnostics with
            {
                IsUpdateCacheDirectoryPresent = Directory.Exists(_paths.UpdateCacheDirectory),
                HasPendingUpdate = pendingUpdate is not null,
                PendingVersion = pendingUpdate?.Version,
                PendingInstallerSizeBytes = pendingUpdate?.SizeBytes,
            };
        }

        private static DesktopAuthDiagnosticsSnapshot CreateAuthDiagnosticsSnapshot()
        {
            return DesktopAuthDiagnosticsState.Snapshot();
        }

        private DesktopDataPathSnapshot CreateDataPathSnapshot()
        {
            return new DesktopDataPathSnapshot(
                _paths.DataDirectory,
                _paths.AppDatabasePath,
                _paths.SyncStateDatabasePath,
                _paths.TokenStorePath);
        }

        private static DesktopRuntimeHealthSnapshot CreateRuntimeHealthSnapshot()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new DesktopRuntimeHealthSnapshot(
                process.Id,
                process.ProcessName,
                process.WorkingSet64,
                TryReadInt64(() => process.PrivateMemorySize64),
                TryReadInt32(() => process.Threads.Count),
                TryReadInt32(() => process.HandleCount));
        }

        private DesktopSyncLifecycleDiagnosticsSnapshot CreateSyncLifecycleDiagnosticsSnapshot(
            IReadOnlyList<SyncPairSettings> syncPairs)
        {
            DesktopSyncApplicationHost? host = _host;
            bool isSignedIn = host is not null;
            string syncCoreState = isSignedIn ? _syncCoreState : SyncCoreStateSignedOut;
            bool isBackgroundActive = isSignedIn
                && (string.Equals(syncCoreState, SyncCoreStateStarting, StringComparison.Ordinal)
                    || string.Equals(syncCoreState, SyncCoreStateRunning, StringComparison.Ordinal));
            int enabledSyncPairCount = syncPairs.Count(static syncPair => syncPair.IsEnabled);
            bool hasNoSyncPairs = syncPairs.Count == 0;
            bool isZeroPairBackgroundActive = hasNoSyncPairs && isBackgroundActive;
            string status;
            string details;
            if (!isSignedIn)
            {
                status = "signedOut";
                details = "Signed out; sync background is not running.";
            }
            else if (isZeroPairBackgroundActive)
            {
                status = "zeroPairBackgroundActive";
                details = "Signed in with no configured sync pairs; sync background is active.";
            }
            else if (hasNoSyncPairs)
            {
                status = "zeroPairBackgroundInactive";
                details = "Signed in with no configured sync pairs; sync background is not active.";
            }
            else
            {
                status = "configuredPairs";
                details = "Signed in with configured sync pairs.";
            }

            return new DesktopSyncLifecycleDiagnosticsSnapshot(
                isSignedIn,
                syncCoreState,
                isBackgroundActive,
                syncPairs.Count,
                enabledSyncPairCount,
                hasNoSyncPairs,
                isZeroPairBackgroundActive,
                status,
                details);
        }

        private static long? TryReadInt64(Func<long> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static int? TryReadInt32(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = Math.Max(0, bytes);
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            string format = unitIndex == 0 || value >= 10 ? "0" : "0.0";
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }
    }
}
