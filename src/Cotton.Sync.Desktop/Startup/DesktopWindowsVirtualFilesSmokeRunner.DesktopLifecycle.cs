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
        private static async Task<int> RunDesktopRootLifecycleAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesNativeApi? nativeApi,
            SyncPairSettings baseSyncPair,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (nativeApi is null)
            {
                await output.WriteLineAsync(
                        FormatCheck(false, "Desktop root lifecycle smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string baseRootPath = NormalizeFullPath(baseSyncPair.LocalRootPath);
            string rootPath = string.Equals(
                    Path.GetFileName(baseRootPath),
                    DesktopRootDirectoryName,
                    StringComparison.OrdinalIgnoreCase)
                ? baseRootPath
                : Path.Combine(baseRootPath, DesktopRootDirectoryName);
            SyncPairSettings syncPair = CreateDesktopRootSyncPair(rootPath);
            string remoteFilePath = ToFullPath(rootPath, DesktopRootRemoteFilePath);
            byte[] remoteContent = Encoding.UTF8.GetBytes("Cotton Sync Desktop root lifecycle cloud file\n");
            string remoteHash = Convert.ToHexStringLower(SHA256.HashData(remoteContent));
            Dictionary<string, byte[]> contentByPath = new(StringComparer.OrdinalIgnoreCase)
            {
                [SyncPath.Normalize(DesktopRootRemoteFilePath)] = remoteContent,
            };
            DictionarySmokeContentProvider contentProvider = new(contentByPath);
            WindowsCloudFilesHydrationCoordinator callbackHandler = new(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-desktop-root-temp"),
                diagnostics);
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            InMemoryAppActivityPublisher activityPublisher = new();
            InMemoryAppTransferProgressPublisher transferProgressPublisher = new();
            InMemoryAppRunProgressPublisher runProgressPublisher = new();
            RecordingRunProgressObserver runProgressObserver = new();
            IDisposable runProgressSubscription = runProgressPublisher.Subscribe(runProgressObserver);
            LocalChangeSuppression localChangeSuppression = new();
            SyncApplicationService? app = null;
            bool pairDeleted = false;
            bool syncCoreStopped = false;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await pairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await pairStore.DeleteAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                        FormatCheck(true, "Isolated Desktop QA root prepared.")
                        + " root="
                        + rootPath)
                    .ConfigureAwait(false);

                RemoteFilePlaceholderRequest remoteFile = CreatePlaceholderRequest(
                    syncPair,
                    DesktopRootRemoteFilePath,
                    remoteContent.LongLength,
                    remoteHash);
                RemoteTreeSnapshot remoteTree = new()
                {
                    RootNode = new NodeDto
                    {
                        Id = syncPair.RemoteRootNodeId,
                        Name = DesktopRootDirectoryName,
                    },
                    Files =
                    {
                        new RemoteFileSnapshot
                        {
                            RelativePath = DesktopRootRemoteFilePath,
                            File = remoteFile.RemoteFile,
                        },
                    },
                };
                SinglePathRemoteTreeCrawler crawler = new(remoteTree);
                NoTransferRemoteFileSynchronizer remoteFiles = new();
                DesktopCloudFilesPlaceholderWriter placeholderWriter = new(
                    cloudFilesAdapter: cloudFiles,
                    getCapabilities: static () => new SyncPairModeCapabilitySnapshot(true, "Windows Cloud Files API is available."),
                    localChangeSuppression: localChangeSuppression);
                SyncEngine syncEngine = new(
                    new LocalFileScanner(),
                    crawler,
                    remoteFiles,
                    stateStore,
                    remoteFilePlaceholderWriter: placeholderWriter);
                ISyncPairWork pairWork = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                    new WindowsVirtualFilesUploadFinalizationPairWork(
                        new SyncEnginePairWork(
                            syncEngine,
                            activityPublisher,
                            transferProgressPublisher,
                            runProgressPublisher),
                        activityPublisher,
                        stateStore,
                        cloudFiles,
                        localChangeSuppression,
                        runProgressPublisher),
                    stateStore,
                    cloudFiles,
                    localChangeSuppression,
                    diagnostics,
                    runProgressPublisher);
                InMemoryAppStatusPublisher statusPublisher = new();
                app = CreateDesktopRootLifecycleApplication(
                    pairStore,
                    stateStore,
                    cloudFiles,
                    callbackHandler,
                    pairWork,
                    statusPublisher,
                    diagnostics);

                SyncPairSaveResult saveResult = await app.SaveSyncPairAsync(syncPair, cancellationToken)
                    .ConfigureAwait(false);
                SyncPairSettings? savedPair = await pairStore.GetAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        saveResult.IsSaved
                        && savedPair is not null
                        && string.Equals(savedPair.LocalRootPath, rootPath, StringComparison.OrdinalIgnoreCase)
                        && savedPair.Mode == SyncPairMode.WindowsVirtualFiles,
                        "Desktop root sync pair was saved through the app service.",
                        "mode="
                        + (savedPair?.Mode.ToString() ?? "missing")
                        + ", errors="
                        + saveResult.Validation.Errors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                await app.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                SyncPairStatus? startedStatus = statusPublisher.Current.SyncPairs
                    .FirstOrDefault(item => item.SyncPairId == syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        startedStatus is { State: SyncPairRunState.Idle },
                        "Desktop root sync core started with an idle VFS runner.",
                        "state="
                        + (startedStatus?.State.ToString() ?? "missing"))
                    .ConfigureAwait(false);

                await app.SyncNowAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                SyncPairStatus? syncedStatus = statusPublisher.Current.SyncPairs
                    .FirstOrDefault(item => item.SyncPairId == syncPair.Id);
                SyncStateEntry? remoteFileState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), DesktopRootRemoteFilePath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        syncedStatus is { State: SyncPairRunState.Idle, LastSuccessfulSyncAtUtc: not null },
                        "Desktop root sync pass completed with a successful runner status.",
                        "state="
                        + (syncedStatus?.State.ToString() ?? "missing")
                        + ", lastSuccess="
                        + (syncedStatus?.LastSuccessfulSyncAtUtc.HasValue ?? false).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        File.Exists(remoteFilePath)
                        && remoteFileState is { Kind: SyncEntryKind.File }
                        && IsRemoteOnlyPlaceholderState(remoteFileState.PlaceholderHydrationState),
                        "Desktop root remote file became an online-only placeholder.",
                        "state="
                        + FormatStateSummary(remoteFileState))
                    .ConfigureAwait(false);
                failures += await VerifyRunProgressCompletedFinalizingCloudFilesAsync(
                        output,
                        runProgressObserver.Snapshot(),
                        "Desktop root app lifecycle path")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        null,
                        "Desktop root Cloud Files sync root status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        DesktopRootRemoteFilePath,
                        "Desktop root remote file Cloud Files status was finalized.",
                        allowPartialDirectory: true)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        remoteFilePath,
                        "Desktop root remote file",
                        cancellationToken)
                    .ConfigureAwait(false);

                await app.StopSyncAsync(cancellationToken).ConfigureAwait(false);
                syncCoreStopped = true;
                await output.WriteLineAsync(
                        FormatCheck(true, "Desktop root sync core stopped before restart simulation."))
                    .ConfigureAwait(false);

                InMemoryAppStatusPublisher restartedStatusPublisher = new();
                app = CreateDesktopRootLifecycleApplication(
                    pairStore,
                    stateStore,
                    cloudFiles,
                    callbackHandler,
                    pairWork,
                    restartedStatusPublisher,
                    diagnostics);
                syncCoreStopped = false;
                await app.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                SyncPairStatus? restartedStatus = restartedStatusPublisher.Current.SyncPairs
                    .FirstOrDefault(item => item.SyncPairId == syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        restartedStatus is { State: SyncPairRunState.Idle },
                        "Desktop root sync root reconnected from persisted settings after app restart.",
                        "state="
                        + (restartedStatus?.State.ToString() ?? "missing"))
                    .ConfigureAwait(false);

                string restartedHydratedText = await ReadAllTextThroughExternalProcessAsync(remoteFilePath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        string.Equals(
                            restartedHydratedText,
                            Encoding.UTF8.GetString(remoteContent),
                            StringComparison.Ordinal)
                        && contentProvider.DownloadedPaths.Contains(
                            SyncPath.Normalize(DesktopRootRemoteFilePath),
                            StringComparer.OrdinalIgnoreCase),
                        "Restarted Desktop root callbacks hydrated the persisted placeholder.",
                        "downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                cloudFiles.DehydratePlaceholder(syncPair, DesktopRootRemoteFilePath);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        DesktopRootRemoteFilePath,
                        "Restarted Desktop root placeholder was dehydrated before pair deletion.",
                        allowPartialDirectory: true)
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
                        "Deleting the Desktop root sync pair removed the local placeholder root.",
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
                runProgressSubscription.Dispose();
                if (app is not null && !syncCoreStopped)
                {
                    try
                    {
                        await app.StopSyncAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failures++;
                        await output.WriteLineAsync(
                                FormatCheck(false, "Desktop root sync core cleanup failed.")
                                + " "
                                + CleanSingleLine(exception.Message))
                            .ConfigureAwait(false);
                    }
                }

                if (!pairDeleted)
                {
                    failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
                }
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task<int> RunDesktopSessionRestoreAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesNativeApi? nativeApi,
            SyncPairSettings baseSyncPair,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (nativeApi is null)
            {
                await output.WriteLineAsync(
                        FormatCheck(false, "Desktop session restore smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string baseRootPath = NormalizeFullPath(baseSyncPair.LocalRootPath);
            string rootPath = string.Equals(
                    Path.GetFileName(baseRootPath),
                    DesktopSessionRestoreDirectoryName,
                    StringComparison.OrdinalIgnoreCase)
                ? baseRootPath
                : Path.Combine(baseRootPath, DesktopSessionRestoreDirectoryName);
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
                        snapshot.IsSignedIn && string.Equals(snapshot.AccountName, "smoke", StringComparison.Ordinal),
                        "Desktop startup restored the saved signed-in session.",
                        "signedIn="
                        + snapshot.IsSignedIn.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", account="
                        + (snapshot.AccountName ?? "missing"))
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        factory.CreatedServerUrls.Count == 1
                        && factory.CreatedServerUrls[0] == serverUrl,
                        "Desktop startup used the remembered server for session restore.",
                        "hosts="
                        + factory.CreatedServerUrls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                DesktopSyncPairSnapshot? restoredPair = snapshot.SyncPairs
                    .FirstOrDefault(item => item.Id == syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        restoredPair is not null
                        && string.Equals(restoredPair.LocalPath, rootPath, StringComparison.OrdinalIgnoreCase)
                        && restoredPair.Mode == SyncPairMode.WindowsVirtualFiles,
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
                if (controller is not null)
                {
                    await controller.DisposeAsync().ConfigureAwait(false);
                }

                if (app is not null && !syncCoreStopped)
                {
                    try
                    {
                        await app.StopSyncAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failures++;
                        await output.WriteLineAsync(
                                FormatCheck(false, "Desktop session restore sync core cleanup failed.")
                                + " "
                                + CleanSingleLine(exception.Message))
                            .ConfigureAwait(false);
                    }
                }

                if (!pairDeleted)
                {
                    failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
                }
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
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
                    if (lastState.Value.HasFlag(WindowsCloudFilesPlaceholderState.SyncRoot)
                        && lastState.Value.HasFlag(WindowsCloudFilesPlaceholderState.InSync)
                        && status is { State: SyncPairRunState.Idle })
                    {
                        await output.WriteLineAsync(
                                FormatCheck(true, "Desktop startup reconnected the persisted Cloud Files sync root.")
                                + " state="
                                + lastState.Value
                                + ", runner="
                                + status.State)
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

        private static SyncApplicationService CreateDesktopRootLifecycleApplication(
            ISyncPairSettingsStore pairStore,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesCallbackHandler callbackHandler,
            ISyncPairWork pairWork,
            InMemoryAppStatusPublisher statusPublisher,
            IWindowsCloudFilesDiagnostics diagnostics)
        {
            SyncPairRunnerFactory runnerFactory = new(pairWork);
            SyncSupervisor supervisor = new(pairStore, runnerFactory, statusPublisher);
            WindowsCloudFilesSyncRootConnectionCoordinator connectionCoordinator = new(
                pairStore,
                cloudFiles,
                callbackHandler);
            return new SyncApplicationService(
                pairStore,
                NoopSyncPairPrerequisiteValidator.Instance,
                new NoopAppPreferencesStore(),
                NoopAuthFlow.Instance,
                NoopAppCodeBrowserAuthFlow.Instance,
                supervisor,
                NoopPlatformCommandService.Instance,
                syncCoreLifecycleComponents: [connectionCoordinator],
                syncStateStore: stateStore,
                validator: new SyncPairSettingsValidator(
                    new SyncPairModeCapabilitySnapshot(true, "Windows Cloud Files API is available.")),
                syncPairDeletionHandler: new WindowsCloudFilesSyncPairDeletionHandler(
                    cloudFiles,
                    diagnostics: diagnostics,
                    syncStateStore: stateStore));
        }
}
}
