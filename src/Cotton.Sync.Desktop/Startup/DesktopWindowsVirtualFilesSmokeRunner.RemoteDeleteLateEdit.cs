// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static async Task<int> RunRemoteDeleteLateEditAsync(WindowsVirtualFilesSmokeContext context)
        {
            int failures = 0;
            WindowsCloudFilesConnection? connection = null;
            CancellationToken cancellationToken = context.CancellationToken;
            try
            {
                if (context.NativeApi is null)
                {
                    throw new InvalidOperationException("Remote-delete smoke requires the native Windows Cloud Files API.");
                }

                TryUnregisterExistingRoot(context.CloudFiles, context.SyncPair, context.Output);
                PrepareRoot(context.SyncPair.LocalRootPath);
                SqliteSyncStateStore stateStore = new(context.Paths.SyncStateDatabasePath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(context.SyncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                byte[] originalContent = Encoding.UTF8.GetBytes("original");
                StaticSmokeContentProvider contentProvider = new(originalContent);
                WindowsCloudFilesHydrationCoordinator hydration = new(
                    contentProvider, context.NativeApi,
                    Path.Combine(context.Paths.DataDirectory, "vfs-smoke-temp"), context.Diagnostics);
                connection = context.CloudFiles.ConnectSyncRoot(context.SyncPair, hydration);
                RemoteTreeSnapshot remoteTree = new()
                {
                    RootNode = new NodeDto { Id = context.SyncPair.RemoteRootNodeId, Name = "root" },
                };
                SinglePathRemoteTreeCrawler crawler = new(remoteTree);
                RecordingUploadRemoteFileSynchronizer remoteFiles = new();
                SyncEngine engine = new(new LocalFileScanner(), crawler, remoteFiles, stateStore);
                failures += await RunRemoteDeleteCaseAsync(context, engine, stateStore, crawler, remoteTree,
                    remoteFiles, contentProvider, originalContent, "untouched-online.txt",
                    hydrateBeforePlanning: false, editAfterPlanning: false).ConfigureAwait(false);
                failures += await RunRemoteDeleteCaseAsync(context, engine, stateStore, crawler, remoteTree,
                    remoteFiles, contentProvider, originalContent, "edited-hydrated.txt",
                    hydrateBeforePlanning: true, editAfterPlanning: true).ConfigureAwait(false);
                failures += await RunRemoteDeleteCaseAsync(context, engine, stateStore, crawler, remoteTree,
                    remoteFiles, contentProvider, originalContent, "edited-after-hydration.txt",
                    hydrateBeforePlanning: false, editAfterPlanning: true).ConfigureAwait(false);
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

        private static async Task<int> RunRemoteDeleteCaseAsync(
            WindowsVirtualFilesSmokeContext context,
            SyncEngine engine,
            SqliteSyncStateStore stateStore,
            SinglePathRemoteTreeCrawler crawler,
            RemoteTreeSnapshot remoteTree,
            RecordingUploadRemoteFileSynchronizer remoteFiles,
            StaticSmokeContentProvider contentProvider,
            byte[] originalContent,
            string relativePath,
            bool hydrateBeforePlanning,
            bool editAfterPlanning)
        {
            int failures = 0;
            CancellationToken cancellationToken = context.CancellationToken;
            string fullPath = Path.Combine(context.SyncPair.LocalRootPath, relativePath);
            string pairId = context.SyncPair.Id.ToString("D");
            string originalHash = Convert.ToHexStringLower(SHA256.HashData(originalContent));
            RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(
                context.SyncPair, relativePath, originalContent.LongLength, originalHash);
            request.RemoteFile.Id = Guid.CreateVersion7();
            request.RemoteFile.FileManifestId = Guid.CreateVersion7();
            RemoteFilePlaceholderResult placeholder = context.CloudFiles.CreateFilePlaceholder(request);
            SyncStateEntry baseline = CreatePlaceholderState(context.SyncPair, request, placeholder);
            int initialDownloads = contentProvider.DownloadCount;
            if (hydrateBeforePlanning)
            {
                string text = await context.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                context.CloudFiles.SetInSyncState(context.SyncPair, relativePath);
                baseline.LocalContentHash = originalHash;
                baseline.LocalSizeBytes = originalContent.LongLength;
                baseline.LocalLastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
                baseline.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                failures += await WritePassFailAsync(context.Output,
                    text == Encoding.UTF8.GetString(originalContent) && contentProvider.DownloadCount == initialDownloads + 1,
                    "Remote-delete baseline hydrated through the native provider.", " path=" + relativePath).ConfigureAwait(false);
            }

            await stateStore.UpsertAsync(baseline, cancellationToken).ConfigureAwait(false);
            SyncStateEntry? storedBefore = await stateStore.GetAsync(pairId, relativePath, cancellationToken).ConfigureAwait(false);
            string serializedBefore = JsonSerializer.Serialize(storedBefore);
            int downloadsBeforeRun = contentProvider.DownloadCount;
            int uploadsBeforeRun = remoteFiles.Uploads.Count;
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(fullPath);
            byte[] editedContent = Encoding.UTF8.GetBytes("modified");
            bool changed = false;
            crawler.BeforeCrawlReturnsAsync = async token =>
            {
                if (!editAfterPlanning || changed)
                {
                    return;
                }

                changed = true;
                if (!hydrateBeforePlanning)
                {
                    string hydrated = await context.ReadAllTextAsync(fullPath, token).ConfigureAwait(false);
                    if (hydrated != Encoding.UTF8.GetString(originalContent))
                    {
                        throw new IOException("Late native hydration returned unexpected bytes.");
                    }
                }

                await File.WriteAllBytesAsync(fullPath, editedContent, token).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(fullPath, originalWriteTime);
            };
            SyncPair pair = new()
            {
                SyncPairId = pairId,
                LocalRootPath = context.SyncPair.LocalRootPath,
                RemoteRootNodeId = context.SyncPair.RemoteRootNodeId,
                MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
            };
            SyncRunResult result = await engine.RunOnceAsync(pair,
                new SyncRunOptions { AllowInitialVirtualFilesStreaming = false }, cancellationToken).ConfigureAwait(false);
            crawler.BeforeCrawlReturnsAsync = null;
            SyncStateEntry? storedAfter = await stateStore.GetAsync(pairId, relativePath, cancellationToken).ConfigureAwait(false);
            if (!editAfterPlanning)
            {
                string[] preserved = Directory.GetFiles(Path.Combine(context.SyncPair.LocalRootPath, ".cotton-sync", "deleted"),
                    relativePath, SearchOption.AllDirectories);
                bool retainedPlaceholder = preserved.Length == 1
                    && HasRecallOnDataAccess(File.GetAttributes(preserved[0]))
                    && (File.GetAttributes(preserved[0]) & FileAttributes.ReparsePoint) != 0
                    && new FileInfo(preserved[0]).Length == originalContent.LongLength;
                failures += await WritePassFailAsync(context.Output,
                    !File.Exists(fullPath) && storedAfter is null && !result.HasDeferredLocalPaths && !result.RequiresUserAction
                    && result.Activities.Any(activity => activity.RelativePath == relativePath && activity.Kind == SyncActivityKind.DeletedLocal)
                    && contentProvider.DownloadCount == downloadsBeforeRun && remoteFiles.Uploads.Count == uploadsBeforeRun
                    && retainedPlaceholder,
                    "Untouched online-only remote deletion preserved its placeholder without hydration.",
                    " downloads=" + (contentProvider.DownloadCount - downloadsBeforeRun) + ", preserved=" + preserved.Length).ConfigureAwait(false);
                return failures;
            }

            byte[] actual = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            int expectedDownloads = downloadsBeforeRun;
            if (!hydrateBeforePlanning)
            {
                expectedDownloads++;
            }

            failures += await WritePassFailAsync(context.Output,
                changed && actual.SequenceEqual(editedContent) && JsonSerializer.Serialize(storedAfter) == serializedBefore
                && result.DeferredLocalPaths.Contains(relativePath) && !result.RequiresUserAction
                && !result.Activities.Any(activity => activity.RelativePath == relativePath && activity.Kind == SyncActivityKind.DeletedLocal)
                && result.Activities.Any(activity => activity.RelativePath == relativePath && activity.Kind == SyncActivityKind.Skipped)
                && remoteFiles.Uploads.Count == uploadsBeforeRun && contentProvider.DownloadCount == expectedDownloads,
                "Late local edit survived remote deletion with its baseline retained and path deferred.",
                " path=" + relativePath + ", downloads=" + contentProvider.DownloadCount
                + ", deferred=" + string.Join(',', result.DeferredLocalPaths)).ConfigureAwait(false);

            SyncRunResult repeated = await engine.RunOnceAsync(pair, new SyncRunOptions
            {
                Scope = SyncRunScope.ForLocalChangedPaths(result.DeferredLocalPaths),
                AllowInitialVirtualFilesStreaming = false,
            }, cancellationToken).ConfigureAwait(false);
            actual = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            string editedHash = Convert.ToHexStringLower(SHA256.HashData(editedContent));
            SyncStateEntry? recovered = await stateStore.GetAsync(pairId, relativePath, cancellationToken).ConfigureAwait(false);
            bool uploaded = remoteFiles.Uploads.Count == uploadsBeforeRun + 1
                && remoteFiles.Uploads[^1].RelativePath == relativePath
                && remoteFiles.Uploads[^1].Returned.ContentHash == editedHash;
            failures += await WritePassFailAsync(context.Output,
                uploaded && actual.SequenceEqual(editedContent) && !repeated.HasDeferredLocalPaths
                && repeated.Activities.Any(activity => activity.RelativePath == relativePath && activity.Kind == SyncActivityKind.Conflict)
                && recovered?.LocalContentHash == editedHash && recovered.RemoteContentHash == editedHash,
                "Scoped retry recovered the preserved user bytes through the remote-deletion conflict path.",
                " path=" + relativePath + ", uploads=" + (remoteFiles.Uploads.Count - uploadsBeforeRun)).ConfigureAwait(false);
            if (uploaded)
            {
                remoteTree.Files.Add(new RemoteFileSnapshot { RelativePath = relativePath, File = remoteFiles.Uploads[^1].Returned });
            }

            return failures;
        }
    }
}
