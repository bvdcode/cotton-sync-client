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
        public async Task<DesktopSelfTestSnapshot> RunSelfTestAsync(CancellationToken cancellationToken = default)
        {
            DesktopSelfTestRun run = new();
            await AddStorageSelfTestsAsync(run, cancellationToken).ConfigureAwait(false);
            await AddDesktopCapabilitySelfTestsAsync(run, cancellationToken).ConfigureAwait(false);
            await AddConnectivitySelfTestsAsync(run, cancellationToken).ConfigureAwait(false);
            await AddSyncPairSelfTestsAsync(run, cancellationToken).ConfigureAwait(false);
            return new DesktopSelfTestSnapshot(run.Items);
        }

        private async Task AddStorageSelfTestsAsync(
            DesktopSelfTestRun run,
            CancellationToken cancellationToken)
        {
            await AddSelfTestCheckAsync(
                run.Items,
                "Preferences database",
                async () =>
                {
                    await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    run.Preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
                    return "Ready";
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                run.Items,
                "Sync pair database",
                async () =>
                {
                    await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    run.SyncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
                    return run.SyncPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " sync pair(s)";
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                run.Items,
                "Sync state database",
                async () =>
                {
                    SqliteSyncStateStore stateStore = new(_paths.SyncStateDatabasePath);
                    await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    await stateStore.GetChangeCursorAsync(SelfTestSyncPairId, cancellationToken).ConfigureAwait(false);
                    SyncStateStoreDiagnostics diagnostics = await stateStore.GetDiagnosticsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return CreateSyncStateDatabaseDetails(diagnostics);
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                run.Items,
                "Authentication state",
                () => CheckAuthenticationStateAsync(cancellationToken)).ConfigureAwait(false);
        }

        private async Task AddDesktopCapabilitySelfTestsAsync(
            DesktopSelfTestRun run,
            CancellationToken cancellationToken)
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage = await _tokenStorageVerifier(cancellationToken)
                .ConfigureAwait(false);
            run.Items.Add(new DesktopSelfTestItemSnapshot(
                "Token storage",
                tokenStorage.IsReleaseSecure,
                tokenStorage.IsReleaseSecure
                    ? tokenStorage.Details
                    : tokenStorage.Details + " (not release secure)"));

            await AddSelfTestCheckAsync(
                run.Items,
                "Desktop icon",
                () => CheckDesktopIconAsync(cancellationToken)).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                run.Items,
                "Update cache",
                () => CheckUpdateCacheAsync(_paths.UpdateCacheDirectory, cancellationToken)).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                run.Items,
                "Autostart adapter",
                async () =>
                {
                    bool isEnabled = await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
                    return isEnabled ? "Enabled" : "Disabled";
                }).ConfigureAwait(false);

            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            run.Items.Add(new DesktopSelfTestItemSnapshot(
                "Desktop platform",
                true,
                platformCapabilities.OperatingSystemName
                    + "; session: "
                    + platformCapabilities.DesktopSession
                    + "; desktop: "
                    + platformCapabilities.CurrentDesktop));

            run.Items.Add(new DesktopSelfTestItemSnapshot(
                "Tray lifecycle",
                true,
                platformCapabilities.TrayLifecycleDetails));

            Func<string>? cloudFilesProbeRootFactory = CreateCloudFilesSelfTestProbeRootFactory();
            DesktopCloudFilesSelfTestCapabilitySnapshot modeCapabilities =
                DesktopCloudFilesCapabilities.CreateSelfTestCapability(createProbeRoot: cloudFilesProbeRootFactory);
            run.Items.Add(new DesktopSelfTestItemSnapshot(
                "Windows virtual files",
                modeCapabilities.Passed,
                modeCapabilities.Details,
                Skipped: modeCapabilities.Skipped));

            DesktopNotificationCapabilitySnapshot notificationCapabilities =
                DesktopNotificationServiceFactory.CreateSelfTestCapabilitySnapshot();
            run.Items.Add(CreateNotificationSelfTestItem(notificationCapabilities));

            await AddSelfTestCheckAsync(
                run.Items,
                "File watcher",
                () => CheckFileWatcherAsync(cancellationToken)).ConfigureAwait(false);
        }

        private Func<string>? CreateCloudFilesSelfTestProbeRootFactory()
        {
            if (string.IsNullOrWhiteSpace(_startupOptions.LocalRoot))
            {
                return null;
            }

            string parentRoot = Path.GetFullPath(_startupOptions.LocalRoot.Trim());
            return () => Path.Combine(
                parentRoot,
                ".cotton-cloud-files-self-test-" + Guid.NewGuid().ToString("N"));
        }

        private static Task<string> CheckLocalRootAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(syncPair.LocalRootPath))
            {
                throw new DirectoryNotFoundException("Local root does not exist: " + syncPair.LocalRootPath);
            }

            _ = Directory.EnumerateFileSystemEntries(syncPair.LocalRootPath).Take(1).ToList();
            return Task.FromResult(syncPair.LocalRootPath);
        }

        private static DesktopSelfTestItemSnapshot CreateNotificationSelfTestItem(
            DesktopNotificationCapabilitySnapshot notificationCapabilities)
        {
            ArgumentNullException.ThrowIfNull(notificationCapabilities);
            return new DesktopSelfTestItemSnapshot(
                "Notification adapter",
                notificationCapabilities.SelfTestPassed,
                notificationCapabilities.Details,
                Skipped: notificationCapabilities.SelfTestSkipped);
        }

        private static DesktopSelfTestItemSnapshot CreateSyncStateDiagnosticsItem(
            SyncStateStoreDiagnostics diagnostics)
        {
            try
            {
                return new DesktopSelfTestItemSnapshot(
                    "Sync state database",
                    true,
                    CreateSyncStateDatabaseDetails(diagnostics));
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                return new DesktopSelfTestItemSnapshot(
                    "Sync state database",
                    false,
                    DesktopActionRequiredMessageResolver.FromException(exception));
            }
        }

        private static string CreateCloudFilesRegistrationDetails(
            DesktopCloudFilesRegistrationDiagnosticsSnapshot diagnostics)
        {
            return "virtual pairs="
                + diagnostics.VirtualFilesSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", registered="
                + diagnostics.RegisteredSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", missing="
                + diagnostics.MissingSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", unknown="
                + diagnostics.UnknownSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string CreateSyncStateDatabaseDetails(SyncStateStoreDiagnostics diagnostics)
        {
            if (IsEmptyBloatedStateDatabase(diagnostics))
            {
                throw new InvalidOperationException(
                    "State database has no sync entries or change cursors, but still reserves "
                    + FormatBytes(diagnostics.FileSizeBytes)
                    + " with "
                    + FormatBytes(diagnostics.FreelistBytes)
                    + " free SQLite pages. Database maintenance is required.");
            }

            return "Ready: entries="
                + diagnostics.SyncEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", cursors="
                + diagnostics.SyncChangeCursorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", file="
                + FormatBytes(diagnostics.FileSizeBytes)
                + ", used="
                + FormatBytes(diagnostics.UsedBytes)
                + ", free="
                + FormatBytes(diagnostics.FreelistBytes);
        }

        private static bool IsEmptyBloatedStateDatabase(SyncStateStoreDiagnostics diagnostics)
        {
            return !diagnostics.HasRows
                && diagnostics.FreelistBytes >= EmptyStateDatabaseFreelistWarningBytes
                && diagnostics.FreelistRatio >= EmptyStateDatabaseFreelistWarningRatio;
        }
    }
}
