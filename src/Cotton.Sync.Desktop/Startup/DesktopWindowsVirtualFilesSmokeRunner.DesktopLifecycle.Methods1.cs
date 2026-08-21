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
        private static async Task<int> RunDesktopRootLifecycleAsync(WindowsVirtualFilesSmokeContext context)
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
                        FormatCheck(false, "Desktop root lifecycle smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = ResolveDesktopScenarioRoot(baseSyncPair.LocalRootPath, DesktopRootDirectoryName);
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
                    getCapabilities: CreateAvailableCloudFilesCapability,
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
                        IsDesktopRootPairSaved(saveResult, savedPair, rootPath),
                        "Desktop root sync pair was saved through the app service.",
                        "mode="
                        + FormatSyncPairMode(savedPair)
                        + ", errors="
                        + saveResult.Validation.Errors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                await app.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                SyncPairStatus? startedStatus = FindSyncPairStatus(statusPublisher, syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        startedStatus is { State: SyncPairRunState.Idle },
                        "Desktop root sync core started with an idle VFS runner.",
                        "state="
                        + FormatSyncPairState(startedStatus))
                    .ConfigureAwait(false);

                await app.SyncNowAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                SyncPairStatus? syncedStatus = FindSyncPairStatus(statusPublisher, syncPair.Id);
                SyncStateEntry? remoteFileState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), DesktopRootRemoteFilePath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        syncedStatus is { State: SyncPairRunState.Idle, LastSuccessfulSyncAtUtc: not null },
                        "Desktop root sync pass completed with a successful runner status.",
                        "state="
                        + FormatSyncPairState(syncedStatus)
                        + ", lastSuccess="
                        + HasSuccessfulSync(syncedStatus).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        IsDesktopRemotePlaceholderReady(remoteFilePath, remoteFileState),
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
                SyncPairStatus? restartedStatus = FindSyncPairStatus(restartedStatusPublisher, syncPair.Id);
                failures += await WriteCheckAsync(
                        output,
                        restartedStatus is { State: SyncPairRunState.Idle },
                        "Desktop root sync root reconnected from persisted settings after app restart.",
                        "state="
                        + FormatSyncPairState(restartedStatus))
                    .ConfigureAwait(false);

                string restartedHydratedText = await ReadAllTextThroughExternalProcessAsync(remoteFilePath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        DidRestartedDesktopRootHydrate(restartedHydratedText, remoteContent, contentProvider),
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
                failures += await CleanupDesktopLifecycleAsync(new DesktopLifecycleCleanupContext
                {
                    OwnedController = null,
                    Application = app,
                    SyncCoreStopped = syncCoreStopped,
                    PairDeleted = pairDeleted,
                    CloudFiles = cloudFiles,
                    SyncPair = syncPair,
                    Output = output,
                    StopFailureLabel = "Desktop root sync core cleanup failed.",
                }).ConfigureAwait(false);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static bool IsDesktopRootPairSaved(
            SyncPairSaveResult saveResult,
            SyncPairSettings? savedPair,
            string expectedRootPath)
        {
            return saveResult.IsSaved
                && savedPair is not null
                && string.Equals(savedPair.LocalRootPath, expectedRootPath, StringComparison.OrdinalIgnoreCase)
                && savedPair.Mode == SyncPairMode.WindowsVirtualFiles;
        }

        private static SyncPairModeCapabilitySnapshot CreateAvailableCloudFilesCapability()
        {
            return new SyncPairModeCapabilitySnapshot(true, "Windows Cloud Files API is available.");
        }

        private static SyncPairStatus? FindSyncPairStatus(InMemoryAppStatusPublisher publisher, Guid syncPairId)
        {
            return publisher.Current.SyncPairs.FirstOrDefault(item => item.SyncPairId == syncPairId);
        }

        private static string FormatSyncPairMode(SyncPairSettings? syncPair)
        {
            return syncPair is null ? "missing" : syncPair.Mode.ToString();
        }

        private static string FormatSyncPairState(SyncPairStatus? status)
        {
            return status is null ? "missing" : status.State.ToString();
        }

        private static bool HasSuccessfulSync(SyncPairStatus? status)
        {
            return status?.LastSuccessfulSyncAtUtc.HasValue == true;
        }

        private static bool IsDesktopRemotePlaceholderReady(string filePath, SyncStateEntry? state)
        {
            return File.Exists(filePath)
                && state is { Kind: SyncEntryKind.File }
                && IsRemoteOnlyPlaceholderState(state.PlaceholderHydrationState);
        }
    }
}
