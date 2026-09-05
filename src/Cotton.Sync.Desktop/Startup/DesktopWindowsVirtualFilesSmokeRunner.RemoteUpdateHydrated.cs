// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private const string HydratedUpdateRelativePath = "hydrated-remote-update.txt";

        private static async Task<int> RunRemoteUpdateHydratedAsync(WindowsVirtualFilesSmokeContext context)
        {
            string filePath = Path.Combine(context.SyncPair.LocalRootPath, HydratedUpdateRelativePath);
            SqliteSyncStateStore stateStore = new(context.Paths.SyncStateDatabasePath);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;
            CancellationToken cancellationToken = context.CancellationToken;
            try
            {
                if (context.NativeApi is null)
                {
                    throw new InvalidOperationException("Hydrated remote-update smoke requires the native Windows Cloud Files API.");
                }

                TryUnregisterExistingRoot(context.CloudFiles, context.SyncPair, context.Output);
                PrepareRoot(context.SyncPair.LocalRootPath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(context.SyncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                byte[] originalContent = Encoding.UTF8.GetBytes("Cotton Sync hydrated original content\n");
                StaticSmokeContentProvider contentProvider = new(originalContent);
                WindowsCloudFilesHydrationCoordinator hydration = new(
                    contentProvider,
                    context.NativeApi,
                    Path.Combine(context.Paths.DataDirectory, "vfs-smoke-temp"),
                    context.Diagnostics);
                connection = context.CloudFiles.ConnectSyncRoot(context.SyncPair, hydration);
                RemoteFilePlaceholderRequest original = CreateHydratedUpdateRequest(context, originalContent);
                RemoteFilePlaceholderResult placeholder = context.CloudFiles.CreateFilePlaceholder(original);
                string hydratedText = await context.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                failures += await WritePassFailAsync(
                    context.Output,
                    hydratedText == Encoding.UTF8.GetString(originalContent) && contentProvider.DownloadCount == 1,
                    "Remote-update baseline was hydrated through the native provider.",
                    " downloads=" + contentProvider.DownloadCount).ConfigureAwait(false);
                context.CloudFiles.SetInSyncState(context.SyncPair, HydratedUpdateRelativePath);
                SyncStateEntry originalState = CreatePlaceholderState(context.SyncPair, original, placeholder);
                originalState.LocalContentHash = original.RemoteFile.ContentHash;
                originalState.LocalSizeBytes = originalContent.LongLength;
                originalState.LocalLastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                originalState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                await stateStore.UpsertAsync(originalState, cancellationToken).ConfigureAwait(false);
                failures += await VerifyHydratedUpdateStateAsync(context, stateStore, original.RemoteFile, "before download")
                    .ConfigureAwait(false);

                byte[] updatedContent = Encoding.UTF8.GetBytes("Cotton Sync downloaded remote replacement\n");
                RemoteFilePlaceholderRequest updated = CreateHydratedUpdateRequest(context, updatedContent);
                RemoteTreeSnapshot remoteTree = new()
                {
                    RootNode = new NodeDto { Id = context.SyncPair.RemoteRootNodeId, Name = "root" },
                    Files = { new RemoteFileSnapshot { RelativePath = HydratedUpdateRelativePath, File = updated.RemoteFile } },
                };
                HydratedUpdateRemoteFileSynchronizer remoteFiles = new() { Content = updatedContent };
                InMemoryAppActivityPublisher activityPublisher = new();
                InMemoryAppRunProgressPublisher runProgressPublisher = new();
                LocalChangeSuppression suppression = new();
                SyncEngine engine = new(new LocalFileScanner(), new SinglePathRemoteTreeCrawler(remoteTree), remoteFiles, stateStore);
                ISyncPairWork pairWork = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                    new WindowsVirtualFilesUploadFinalizationPairWork(
                        new SyncEnginePairWork(engine, activityPublisher, new InMemoryAppTransferProgressPublisher(), runProgressPublisher),
                        activityPublisher, stateStore, context.CloudFiles, suppression, runProgressPublisher),
                    stateStore, context.CloudFiles, suppression, context.Diagnostics, runProgressPublisher);

                await pairWork.RunOnceAsync(context.SyncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
                failures += await VerifyHydratedUpdateContentAsync(context, updatedContent, "Remote update replaced hydrated file bytes.")
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(context.Output, remoteFiles.DownloadCount == 1 && remoteFiles.Uploads.Count == 0,
                    "Hydrated remote update used the sync engine download path.",
                    " downloads=" + remoteFiles.DownloadCount + ", uploads=" + remoteFiles.Uploads.Count).ConfigureAwait(false);
                failures += await VerifyHydratedUpdateStateAsync(context, stateStore, updated.RemoteFile, "after download")
                    .ConfigureAwait(false);

                SyncRunRequest targetRequest = SyncRunRequest.ForLocalChangedPaths([HydratedUpdateRelativePath]);
                await pairWork.RunOnceAsync(context.SyncPair, targetRequest, cancellationToken).ConfigureAwait(false);
                failures += await VerifyHydratedUpdateStateAsync(context, stateStore, updated.RemoteFile, "after repeat pass")
                    .ConfigureAwait(false);
                failures += await WritePassFailAsync(context.Output, remoteFiles.DownloadCount == 1 && remoteFiles.Uploads.Count == 0,
                    "Repeated hydrated remote-update pass converged without transfers.", string.Empty).ConfigureAwait(false);

                byte[] concurrentContent = Encoding.UTF8.GetBytes("Cotton Sync user edit during remote download\n");
                byte[] secondRemoteContent = Encoding.UTF8.GetBytes("Cotton Sync second remote replacement\n");
                RemoteFilePlaceholderRequest secondRemote = CreateHydratedUpdateRequest(context, secondRemoteContent);
                remoteTree.Files[0].File = secondRemote.RemoteFile;
                remoteFiles.Content = secondRemoteContent;
                remoteFiles.BeforeDownloadAsync = token => File.WriteAllBytesAsync(filePath, concurrentContent, token);
                await pairWork.RunOnceAsync(context.SyncPair, targetRequest, cancellationToken).ConfigureAwait(false);
                remoteFiles.BeforeDownloadAsync = null;
                failures += await VerifyHydratedUpdateContentAsync(context, secondRemoteContent,
                    "Concurrent hydrated update retained downloaded bytes at the original path.").ConfigureAwait(false);
                string[] conflictPaths = Directory.GetFiles(context.SyncPair.LocalRootPath, "*Cotton conflict*");
                bool conflictPreserved = conflictPaths.Length == 1
                    && (await File.ReadAllBytesAsync(conflictPaths[0], cancellationToken).ConfigureAwait(false)).SequenceEqual(concurrentContent);
                failures += await WritePassFailAsync(context.Output, conflictPreserved,
                    "Concurrent hydrated edit was preserved in the existing conflict-copy format.",
                    " conflicts=" + conflictPaths.Length).ConfigureAwait(false);
                failures += await VerifyHydratedUpdateStateAsync(context, stateStore, secondRemote.RemoteFile, "after concurrent download")
                    .ConfigureAwait(false);

                byte[] laterLocalContent = Encoding.UTF8.GetBytes("Cotton Sync later local edit after remote replacement\n");
                await File.WriteAllBytesAsync(filePath, laterLocalContent, cancellationToken).ConfigureAwait(false);
                await pairWork.RunOnceAsync(context.SyncPair, targetRequest, cancellationToken).ConfigureAwait(false);
                bool uploaded = remoteFiles.Uploads.Count == 1
                    && remoteFiles.Uploads[0].Returned.ContentHash == CreateHydratedUpdateRequest(context, laterLocalContent).RemoteFile.ContentHash;
                failures += await WritePassFailAsync(context.Output, uploaded,
                    "Local edit after hydrated replacement uploaded once.", " uploads=" + remoteFiles.Uploads.Count).ConfigureAwait(false);
                if (uploaded)
                {
                    NodeFileManifestDto uploadedFile = remoteFiles.Uploads[0].Returned;
                    remoteTree.Files[0].File = uploadedFile;
                    await pairWork.RunOnceAsync(context.SyncPair, targetRequest, cancellationToken).ConfigureAwait(false);
                    failures += await VerifyHydratedUpdateStateAsync(context, stateStore, uploadedFile, "after local edit convergence")
                        .ConfigureAwait(false);
                    failures += await VerifyHydratedUpdateContentAsync(context, laterLocalContent,
                        "Local edit after hydrated replacement remained intact after repeat pass.").ConfigureAwait(false);
                    failures += await WritePassFailAsync(context.Output, remoteFiles.Uploads.Count == 1 && remoteFiles.DownloadCount == 2,
                        "Hydrated replacement and subsequent local edit converged without duplicate transfers.", string.Empty).ConfigureAwait(false);
                    failures += await VerifyExplorerShellSettledStatusAsync(context.Output, filePath,
                        "hydrated remote-update file", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(context.Output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                failures += TryUnregisterSmokeRoot(context.CloudFiles, context.SyncPair, context.Output);
            }

            return await WriteSmokeResultAsync(context.Output, context.Diagnostics, failures).ConfigureAwait(false);
        }
    }
}
