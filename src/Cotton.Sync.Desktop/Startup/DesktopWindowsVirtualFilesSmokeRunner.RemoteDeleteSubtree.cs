// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Models.Enums;
using Cotton.Nodes;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static async Task<int> RunConfirmedRemoteSubtreeDeleteAsync(
            WindowsVirtualFilesSmokeContext context,
            SyncEngine engine,
            SqliteSyncStateStore stateStore,
            StaticSmokeContentProvider contentProvider,
            byte[] originalContent)
        {
            const string folderPath = "deleted-archive";
            const int fileCount = 101;
            string pairId = context.SyncPair.Id.ToString("D");
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(originalContent));
            NodeDto folder = new()
            {
                Id = Guid.NewGuid(), ParentId = context.SyncPair.RemoteRootNodeId, Name = folderPath,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            context.CloudFiles.CreateDirectoryPlaceholder(new RemoteDirectoryMaterializationRequest(
                pairId, context.SyncPair.LocalRootPath, context.SyncPair.RemoteRootNodeId, folderPath, folder));
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = pairId, RelativePath = folderPath, Kind = SyncEntryKind.Directory,
                RemoteNodeId = folder.Id, SyncedAtUtc = DateTime.UtcNow,
            }, context.CancellationToken).ConfigureAwait(false);
            for (int index = 0; index < fileCount; index++)
            {
                string path = folderPath + "/file-" + index + ".bin";
                RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(context.SyncPair, path, originalContent.LongLength, contentHash);
                request.RemoteFile.Id = Guid.NewGuid();
                request.RemoteFile.NodeId = folder.Id;
                request.RemoteFile.FileManifestId = Guid.NewGuid();
                RemoteFilePlaceholderResult placeholder = context.CloudFiles.CreateFilePlaceholder(request);
                await stateStore.UpsertAsync(CreatePlaceholderState(context.SyncPair, request, placeholder),
                    context.CancellationToken).ConfigureAwait(false);
            }
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
            [
                new SyncChangeDto
                {
                    Id = 1, Kind = SyncChangeKind.FolderDeleted, ItemId = folder.Id, Name = folderPath,
                    ParentNodeId = context.SyncPair.RemoteRootNodeId, LayoutId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                },
            ]);
            int downloadsBefore = contentProvider.DownloadCount;
            SyncRunResult result = await engine.RunOnceAsync(new SyncPair
            {
                SyncPairId = pairId, LocalRootPath = context.SyncPair.LocalRootPath,
                RemoteRootNodeId = context.SyncPair.RemoteRootNodeId,
                MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
            }, new SyncRunOptions { RemoteChanges = changes }, context.CancellationToken).ConfigureAwait(false);
            bool baselineRemoved = await stateStore.GetAsync(pairId, folderPath, context.CancellationToken)
                .ConfigureAwait(false) is null;
            bool passed = !result.RequiresUserAction && !result.HasDeferredLocalPaths
                && !Directory.Exists(Path.Combine(context.SyncPair.LocalRootPath, folderPath))
                && baselineRemoved && contentProvider.DownloadCount == downloadsBefore
                && result.Activities.Count(activity => activity.Kind == SyncActivityKind.DeletedLocal) == fileCount + 1;
            return await WritePassFailAsync(context.Output, passed,
                "Confirmed cloud folder deletion removed 101 native online-only files without hydration or approval.",
                " activities=" + result.TotalActivityCount + ", pending=" + result.RequiresUserAction)
                .ConfigureAwait(false);
        }
    }
}
