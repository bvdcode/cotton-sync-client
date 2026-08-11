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
        private static async Task<int> RunNonEmptyPreservationAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string rootLocalFilePath = Path.Combine(
                rootPath,
                NonEmptyPreservationRootFilePath.Replace('/', Path.DirectorySeparatorChar));
            string nestedLocalFilePath = Path.Combine(
                rootPath,
                NonEmptyPreservationNestedFilePath.Replace('/', Path.DirectorySeparatorChar));
            string remoteOnlyFilePath = Path.Combine(
                rootPath,
                NonEmptyPreservationRemoteOnlyFilePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] rootLocalContent = Encoding.UTF8.GetBytes("Cotton Sync pre-existing root file\n");
            byte[] nestedLocalContent = Encoding.UTF8.GetBytes("Cotton Sync pre-existing nested file\n");
            byte[] remoteOnlyContent = Encoding.UTF8.GetBytes("Cotton Sync remote-only content\n");
            string rootLocalHash = Convert.ToHexStringLower(SHA256.HashData(rootLocalContent));
            string nestedLocalHash = Convert.ToHexStringLower(SHA256.HashData(nestedLocalContent));
            string remoteOnlyHash = Convert.ToHexStringLower(SHA256.HashData(remoteOnlyContent));
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var transferProgressPublisher = new InMemoryAppTransferProgressPublisher();
            var runProgressPublisher = new InMemoryAppRunProgressPublisher();
            RecordingRunProgressObserver runProgressObserver = new();
            IDisposable runProgressSubscription = runProgressPublisher.Subscribe(runProgressObserver);
            var localChangeSuppression = new LocalChangeSuppression();
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(rootLocalFilePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(nestedLocalFilePath)!);
                await File.WriteAllBytesAsync(rootLocalFilePath, rootLocalContent, cancellationToken)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(nestedLocalFilePath, nestedLocalContent, cancellationToken)
                    .ConfigureAwait(false);
                DateTime oldLocalWriteTime = DateTime.UtcNow - TimeSpan.FromSeconds(10);
                File.SetLastWriteTimeUtc(rootLocalFilePath, oldLocalWriteTime);
                File.SetLastWriteTimeUtc(nestedLocalFilePath, oldLocalWriteTime);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated non-empty QA root prepared.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for non-empty preservation smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                RemoteFilePlaceholderRequest remoteOnlyRequest = CreatePlaceholderRequest(
                    syncPair,
                    NonEmptyPreservationRemoteOnlyFilePath,
                    remoteOnlyContent.LongLength,
                    remoteOnlyHash);
                var remoteTree = new RemoteTreeSnapshot
                {
                    RootNode = new NodeDto
                    {
                        Id = syncPair.RemoteRootNodeId,
                        Name = "root",
                    },
                    Directories =
                    {
                        new RemoteDirectorySnapshot
                        {
                            RelativePath = NonEmptyPreservationRemoteOnlyDirectoryName,
                            Node = CreateDirectoryRequest(syncPair, NonEmptyPreservationRemoteOnlyDirectoryName).RemoteDirectory,
                        },
                    },
                    Files =
                    {
                        new RemoteFileSnapshot
                        {
                            RelativePath = NonEmptyPreservationRemoteOnlyFilePath,
                            File = remoteOnlyRequest.RemoteFile,
                        },
                    },
                };
                var crawler = new SinglePathRemoteTreeCrawler(remoteTree);
                var remoteFiles = new RecordingUploadRemoteFileSynchronizer();
                var remoteDirectories = new RecordingRemoteDirectorySynchronizer(syncPair.RemoteRootNodeId);
                var placeholderWriter = new DesktopCloudFilesPlaceholderWriter(
                    cloudFilesAdapter: cloudFiles,
                    getCapabilities: static () => new SyncPairModeCapabilitySnapshot(true, "Windows Cloud Files API is available."),
                    localChangeSuppression: localChangeSuppression);
                var syncEngine = new SyncEngine(
                    new LocalFileScanner(),
                    crawler,
                    remoteFiles,
                    stateStore,
                    remoteDirectories: remoteDirectories,
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

                await pairWork.RunOnceAsync(syncPair, cancellationToken).ConfigureAwait(false);

                failures += await VerifyRunProgressCompletedFinalizingCloudFilesAsync(
                        output,
                        runProgressObserver.Snapshot(),
                        "non-empty preservation app sync path")
                    .ConfigureAwait(false);

                FileContentHash rootAfter = await ReadFileHashThroughExternalProcessAsync(rootLocalFilePath, cancellationToken)
                    .ConfigureAwait(false);
                FileContentHash nestedAfter = await ReadFileHashThroughExternalProcessAsync(nestedLocalFilePath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(
                        output,
                        string.Equals(rootAfter.Sha256, rootLocalHash, StringComparison.OrdinalIgnoreCase)
                        && rootAfter.Length == rootLocalContent.LongLength,
                        "Pre-existing root file survived with identical content.",
                        " sha256="
                        + rootAfter.Sha256)
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(
                        output,
                        string.Equals(nestedAfter.Sha256, nestedLocalHash, StringComparison.OrdinalIgnoreCase)
                        && nestedAfter.Length == nestedLocalContent.LongLength,
                        "Pre-existing nested file survived with identical content.",
                        " sha256="
                        + nestedAfter.Sha256)
                    .ConfigureAwait(false);

                SyncStateEntry? rootFileState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), NonEmptyPreservationRootFilePath, cancellationToken)
                    .ConfigureAwait(false);
                SyncStateEntry? nestedFileState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), NonEmptyPreservationNestedFilePath, cancellationToken)
                    .ConfigureAwait(false);
                SyncStateEntry? remoteOnlyState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), NonEmptyPreservationRemoteOnlyFilePath, cancellationToken)
                    .ConfigureAwait(false);
                bool uploadedLocalFiles = remoteFiles.Uploads.Count == 2
                    && remoteFiles.Uploads.Any(upload =>
                        string.Equals(upload.RelativePath, NonEmptyPreservationRootFilePath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(upload.Returned.ContentHash, rootLocalHash, StringComparison.OrdinalIgnoreCase))
                    && remoteFiles.Uploads.Any(upload =>
                        string.Equals(upload.RelativePath, NonEmptyPreservationNestedFilePath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(upload.Returned.ContentHash, nestedLocalHash, StringComparison.OrdinalIgnoreCase))
                    && rootFileState is { Kind: SyncEntryKind.File }
                    && nestedFileState is { Kind: SyncEntryKind.File };
                failures += await WritePassFailAsync(
                        output,
                        uploadedLocalFiles,
                        "Pre-existing local files uploaded and received sync baselines.",
                        " uploads="
                        + remoteFiles.Uploads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", uploaded="
                        + string.Join(
                            ";",
                            remoteFiles.Uploads.Select(static upload => upload.RelativePath + ":" + upload.Returned.ContentHash))
                        + ", rootState="
                        + FormatStateSummary(rootFileState)
                        + ", nestedState="
                        + FormatStateSummary(nestedFileState))
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(
                        output,
                        remoteDirectories.Creates.Count >= 2,
                        "Pre-existing local directory tree received remote directory baselines.",
                        " directoriesCreated="
                        + remoteDirectories.Creates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        NonEmptyPreservationDirectoryName,
                        "Pre-existing top-level directory Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        NonEmptyPreservationRemoteOnlyDirectoryName,
                        "Remote-only directory Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        Path.Combine(rootPath, NonEmptyPreservationDirectoryName),
                        "non-empty preservation uploaded top-level directory",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        Path.Combine(rootPath, NonEmptyPreservationRemoteOnlyDirectoryName),
                        "non-empty preservation remote-only directory",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(
                        output,
                        File.Exists(remoteOnlyFilePath)
                        && remoteOnlyState is { Kind: SyncEntryKind.File }
                        && IsRemoteOnlyPlaceholderState(remoteOnlyState.PlaceholderHydrationState),
                        "Remote-only file became an online-only placeholder.",
                        " state="
                        + FormatStateSummary(remoteOnlyState)
                        + ", path="
                        + NonEmptyPreservationRemoteOnlyFilePath)
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        NonEmptyPreservationRemoteOnlyFilePath,
                        "Remote-only placeholder Cloud Files status was finalized.",
                        allowPartialDirectory: true)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        remoteOnlyFilePath,
                        "non-empty preservation remote-only placeholder",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                runProgressSubscription.Dispose();
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static SyncStateEntry CreatePlaceholderState(
            SyncPairSettings syncPair,
            RemoteFilePlaceholderRequest request,
            RemoteFilePlaceholderResult placeholder)
        {
            SyncPlaceholderHydrationState hydrationState = placeholder.HydrationState == SyncPlaceholderHydrationState.None
                ? SyncPlaceholderHydrationState.RemoteOnly
                : placeholder.HydrationState;
            bool materialized = hydrationState == SyncPlaceholderHydrationState.Hydrated;

            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = Cotton.Sync.State.SyncPath.Normalize(request.RelativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = materialized ? request.RemoteFile.ContentHash : null,
                LocalLastWriteUtc = materialized
                    ? placeholder.LocalLastWriteUtc?.ToUniversalTime() ?? request.RemoteFile.UpdatedAt.ToUniversalTime()
                    : null,
                LocalSizeBytes = materialized ? placeholder.LocalSizeBytes ?? request.RemoteFile.SizeBytes : null,
                RemoteSizeBytes = request.RemoteFile.SizeBytes,
                RemoteFileId = request.RemoteFile.Id,
                RemoteNodeId = request.RemoteFile.NodeId,
                RemoteFileManifestId = request.RemoteFile.FileManifestId,
                RemoteOriginalNodeFileId = request.RemoteFile.OriginalNodeFileId,
                RemoteContentHash = request.RemoteFile.ContentHash,
                RemoteETag = request.RemoteFile.ETag,
                PlaceholderIdentity = placeholder.PlaceholderIdentity,
                PlaceholderHydrationState = hydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }
}
}
