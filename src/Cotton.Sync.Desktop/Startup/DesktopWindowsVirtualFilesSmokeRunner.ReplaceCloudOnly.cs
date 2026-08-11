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
        private static async Task<int> RunReplaceCloudOnlyUploadAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string filePath = Path.Combine(
                rootPath,
                ReplaceCloudOnlyRelativePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] oldContent = Encoding.UTF8.GetBytes("Cotton Sync old remote content\n");
            byte[] replacementContent = Encoding.UTF8.GetBytes("Cotton Sync local replacement content\n");
            string oldHash = Convert.ToHexStringLower(SHA256.HashData(oldContent));
            string replacementHash = Convert.ToHexStringLower(SHA256.HashData(replacementContent));
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var transferProgressPublisher = new InMemoryAppTransferProgressPublisher();
            var runProgressPublisher = new InMemoryAppRunProgressPublisher();
            var localChangeSuppression = new LocalChangeSuppression();
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for cloud-only replacement upload smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);
                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for cloud-only replacement upload smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                cloudFiles.CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, ReplaceCloudOnlyDirectoryName));
                await stateStore
                    .UpsertAsync(CreateDirectoryState(syncPair, ReplaceCloudOnlyDirectoryName), cancellationToken)
                    .ConfigureAwait(false);
                RemoteFilePlaceholderRequest oldRemoteRequest = CreatePlaceholderRequest(
                    syncPair,
                    ReplaceCloudOnlyRelativePath,
                    oldContent.LongLength,
                    oldHash);
                RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(oldRemoteRequest);
                await stateStore
                    .UpsertAsync(
                        CreatePlaceholderState(syncPair, oldRemoteRequest, placeholder),
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud-only replacement smoke seeded remote-only baseline.")
                    + " path="
                    + ReplaceCloudOnlyRelativePath
                    + ", identityBytes="
                    + (placeholder.PlaceholderIdentity?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                File.Delete(filePath);
                await File.WriteAllBytesAsync(filePath, replacementContent, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow - TimeSpan.FromSeconds(5));
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud-only placeholder was replaced by a regular local file before sync.")
                    + " path="
                    + ReplaceCloudOnlyRelativePath
                    + ", sha256="
                    + replacementHash
                    + ", attributes="
                    + FormatAttributes(File.GetAttributes(filePath)))
                    .ConfigureAwait(false);

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
                            RelativePath = ReplaceCloudOnlyDirectoryName,
                            Node = CreateDirectoryRequest(syncPair, ReplaceCloudOnlyDirectoryName).RemoteDirectory,
                        },
                    },
                    Files =
                    {
                        new RemoteFileSnapshot
                        {
                            RelativePath = ReplaceCloudOnlyRelativePath,
                            File = oldRemoteRequest.RemoteFile,
                        },
                    },
                };
                var crawler = new SinglePathRemoteTreeCrawler(remoteTree);
                var remoteFiles = new RecordingUploadRemoteFileSynchronizer();
                var syncEngine = new SyncEngine(
                    new LocalFileScanner(),
                    crawler,
                    remoteFiles,
                    stateStore);
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

                await pairWork
                    .RunOnceAsync(
                        syncPair,
                        SyncRunRequest.ForLocalChangedPaths([ReplaceCloudOnlyRelativePath]),
                        cancellationToken)
                    .ConfigureAwait(false);

                SyncStateEntry? syncedState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), ReplaceCloudOnlyRelativePath, cancellationToken)
                    .ConfigureAwait(false);
                bool uploadPassed = remoteFiles.Uploads.Count == 1
                    && string.Equals(remoteFiles.Uploads[0].RelativePath, ReplaceCloudOnlyRelativePath, StringComparison.OrdinalIgnoreCase)
                    && remoteFiles.Uploads[0].ExistingRemoteFile?.Id == oldRemoteRequest.RemoteFile.Id
                    && string.Equals(remoteFiles.Uploads[0].Returned.ContentHash, replacementHash, StringComparison.OrdinalIgnoreCase)
                    && syncedState is not null
                    && string.Equals(syncedState.RemoteContentHash, replacementHash, StringComparison.OrdinalIgnoreCase)
                    && syncedState.RemoteFileManifestId == remoteFiles.Uploads[0].Returned.FileManifestId;
                if (uploadPassed)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Cloud-only replacement uploaded and persisted remote identity.")
                        + " uploads="
                        + remoteFiles.Uploads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", pathLookupCalls="
                        + crawler.PathLookupCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", fullCrawlCalls="
                        + crawler.FullCrawlCalls.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Cloud-only replacement upload did not produce the expected state.")
                        + " uploads="
                        + remoteFiles.Uploads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", hasState="
                        + (syncedState is not null).ToString()
                        + ", stateHash="
                        + (syncedState?.RemoteContentHash ?? "missing"))
                        .ConfigureAwait(false);
                }

                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        ReplaceCloudOnlyRelativePath,
                        "Uploaded replacement file Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        ReplaceCloudOnlyDirectoryName,
                        "Uploaded replacement parent directory Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        relativePath: null,
                        "Uploaded replacement sync root Cloud Files status was finalized.",
                        allowPartialDirectory: true)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        filePath,
                        "uploaded replacement file",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        Path.Combine(rootPath, ReplaceCloudOnlyDirectoryName),
                        "uploaded replacement parent directory",
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
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }
}
}
