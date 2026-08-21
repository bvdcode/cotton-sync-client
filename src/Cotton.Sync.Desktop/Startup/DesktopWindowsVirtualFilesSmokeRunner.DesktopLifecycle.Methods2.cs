// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static bool DidRestartedDesktopRootHydrate(
            string hydratedText,
            byte[] expectedContent,
            DictionarySmokeContentProvider contentProvider)
        {
            return string.Equals(hydratedText, Encoding.UTF8.GetString(expectedContent), StringComparison.Ordinal)
                && contentProvider.DownloadedPaths.Contains(
                    SyncPath.Normalize(DesktopRootRemoteFilePath),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<int> RunDesktopSessionRestoreAsync(WindowsVirtualFilesSmokeContext context)
        {
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            IWindowsCloudFilesNativeApi? nativeApi = context.NativeApi;
            SyncPairSettings baseSyncPair = context.SyncPair;
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            CancellationToken cancellationToken = context.CancellationToken;

            if (nativeApi is null)
            {
                await output.WriteLineAsync(
                        FormatCheck(false, "Desktop session restore smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = ResolveDesktopScenarioRoot(
                baseSyncPair.LocalRootPath,
                DesktopSessionRestoreDirectoryName);
            SyncPairSettings syncPair = CreateDesktopSessionRestoreSyncPair(rootPath);
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            InMemoryAppStatusPublisher statusPublisher = new();
            SyncApplicationService? app = null;
            DesktopShellController? controller = null;
            bool pairDeleted = false;
            bool syncCoreStopped = false;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await pairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await pairStore.DeleteAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                AppPreferences preferences = await preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
                Uri serverUrl = new("https://desktop-session-restore-smoke.example/");
                preferences.RememberedServerUrl = serverUrl;
                preferences.RememberedUsername = "session-restore-smoke";
                preferences.StartWithOperatingSystem = true;
                await preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
                await pairStore.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                        FormatCheck(true, "Persisted startup state prepared for Desktop session restore smoke.")
                        + " root="
                        + rootPath)
                    .ConfigureAwait(false);

                FailOnInnerSyncPairWork sessionRestorePairWork = new(
                    "Desktop session restore smoke must not start a sync/reseed pass.");
                app = CreateDesktopRootLifecycleApplication(
                    pairStore,
                    stateStore,
                    cloudFiles,
                    new NoopCloudFilesCallbackHandler(),
                    sessionRestorePairWork,
                    statusPublisher,
                    diagnostics);
                SessionRestoreMemoryTokenStore tokenStore = new();
                SessionRestoreApplicationFactory factory = new(app, tokenStore, statusPublisher, serverUrl);
                controller = new DesktopShellController(
                    paths,
                    factory,
                    preferencesStore,
                    pairStore,
                    NoopPlatformCommandService.Instance,
                    new SmokeAutostartService(),
                    new DesktopShellControllerOptions
                    {
                        TokenStorageCapabilities = static () => new DesktopTokenStorageCapabilitySnapshot(
                            "smoke-release-secure",
                            true,
                            "Release-secure token storage available"),
                        SavedSessionRestoreTimeout = TimeSpan.FromSeconds(5),
                        SavedSessionRestoreRetryBaseDelay = TimeSpan.FromMilliseconds(100),
                        TokenStorageVerificationTimeout = TimeSpan.FromSeconds(5),
                    });

                DesktopShellSnapshot snapshot = await controller.LoadAsync(cancellationToken).ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        IsDesktopSessionRestored(snapshot),
                        "Desktop startup restored the saved signed-in session.",
                        "signedIn="
                        + snapshot.IsSignedIn.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", account="
                        + (snapshot.AccountName ?? "missing"))
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        DidDesktopSessionUseRememberedServer(factory, serverUrl),
                        "Desktop startup used the remembered server for session restore.",
                        "hosts="
                        + factory.CreatedServerUrls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                DesktopSyncPairSnapshot? restoredPair = snapshot.SyncPairs
                    .FirstOrDefault(item => item.Id == syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        IsRestoredDesktopPairExpected(restoredPair, rootPath),
                        "Desktop startup loaded the persisted virtual-files sync pair.",
                        "pair="
                        + (restoredPair?.DisplayName ?? "missing")
                        + ", mode="
                        + (restoredPair?.Mode.ToString() ?? "missing"))
                    .ConfigureAwait(false);
                failures += await WaitForSessionRestoreSyncRootAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        statusPublisher,
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        sessionRestorePairWork.RunCalls == 0,
                        "Desktop startup restore did not start a full sync or placeholder reseed pass.",
                        "syncRuns="
                        + sessionRestorePairWork.RunCalls.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                await app.DeleteSyncPairAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                pairDeleted = true;
                await app.StopSyncAsync(cancellationToken).ConfigureAwait(false);
                syncCoreStopped = true;
                failures += await VerifyPairDeletedAsync(pairStore, stateStore, syncPair, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        !Directory.Exists(rootPath),
                        "Deleting the restored Desktop session pair removed the local placeholder root.",
                        "rootExists="
                        + Directory.Exists(rootPath).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                failures += await CleanupDesktopLifecycleAsync(new DesktopLifecycleCleanupContext
                {
                    OwnedController = controller,
                    Application = app,
                    SyncCoreStopped = syncCoreStopped,
                    PairDeleted = pairDeleted,
                    CloudFiles = cloudFiles,
                    SyncPair = syncPair,
                    Output = output,
                    StopFailureLabel = "Desktop session restore sync core cleanup failed.",
                }).ConfigureAwait(false);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static bool IsDesktopSessionRestored(DesktopShellSnapshot snapshot)
        {
            return snapshot.IsSignedIn
                && string.Equals(snapshot.AccountName, "smoke", StringComparison.Ordinal);
        }

        private static bool DidDesktopSessionUseRememberedServer(
            SessionRestoreApplicationFactory factory,
            Uri serverUrl)
        {
            return factory.CreatedServerUrls.Count == 1
                && factory.CreatedServerUrls[0] == serverUrl;
        }

        private static bool IsRestoredDesktopPairExpected(DesktopSyncPairSnapshot? pair, string expectedRootPath)
        {
            return pair is not null
                && string.Equals(pair.LocalPath, expectedRootPath, StringComparison.OrdinalIgnoreCase)
                && pair.Mode == SyncPairMode.WindowsVirtualFiles;
        }

        private static string ResolveDesktopScenarioRoot(string configuredRootPath, string scenarioDirectoryName)
        {
            string rootPath = NormalizeFullPath(configuredRootPath);
            return string.Equals(
                    Path.GetFileName(rootPath),
                    scenarioDirectoryName,
                    StringComparison.OrdinalIgnoreCase)
                ? rootPath
                : Path.Combine(rootPath, scenarioDirectoryName);
        }

        private static async Task<int> CleanupDesktopLifecycleAsync(DesktopLifecycleCleanupContext context)
        {
            int failures = 0;
            if (context.OwnedController is not null)
            {
                await context.OwnedController.DisposeAsync().ConfigureAwait(false);
            }

            if (context.Application is not null && !context.SyncCoreStopped)
            {
                try
                {
                    await context.Application.StopSyncAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    await context.Output.WriteLineAsync(
                            FormatCheck(false, context.StopFailureLabel)
                            + " "
                            + CleanSingleLine(exception.Message))
                        .ConfigureAwait(false);
                }
            }

            if (!context.PairDeleted)
            {
                failures += TryUnregisterSmokeRoot(context.CloudFiles, context.SyncPair, context.Output);
            }

            return failures;
        }

        private static async Task<int> WaitForSessionRestoreSyncRootAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            InMemoryAppStatusPublisher statusPublisher,
            CancellationToken cancellationToken)
        {
            WindowsCloudFilesPlaceholderState? lastState = null;
            Exception? lastException = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lastState = cloudFiles.GetPlaceholderState(syncPair);
                    SyncPairStatus? status = statusPublisher.Current.SyncPairs
                        .FirstOrDefault(item => item.SyncPairId == syncPair.Id);
                    if (IsRestoredSyncRootReady(lastState.Value, status))
                    {
                        await output.WriteLineAsync(
                                FormatCheck(true, "Desktop startup reconnected the persisted Cloud Files sync root.")
                                + " state="
                                + lastState.Value
                                + ", runner="
                                + FormatSyncPairState(status))
                            .ConfigureAwait(false);
                        return 0;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastException = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            } while (DateTime.UtcNow < deadline);

            await output.WriteLineAsync(
                    FormatCheck(false, "Desktop startup did not reconnect the persisted Cloud Files sync root.")
                    + " state="
                    + (lastState?.ToString() ?? "missing")
                    + ", error="
                    + (lastException is null ? "none" : CleanSingleLine(lastException.Message)))
                .ConfigureAwait(false);
            return 1;
        }

        private static bool IsRestoredSyncRootReady(
            WindowsCloudFilesPlaceholderState state,
            SyncPairStatus? status)
        {
            return state.HasFlag(WindowsCloudFilesPlaceholderState.SyncRoot)
                && state.HasFlag(WindowsCloudFilesPlaceholderState.InSync)
                && status is { State: SyncPairRunState.Idle };
        }
    }
}
