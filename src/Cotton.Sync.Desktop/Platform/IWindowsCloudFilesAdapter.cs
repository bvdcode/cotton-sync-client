// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesAdapter
    {
        RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request);

        IReadOnlyList<RemoteFilePlaceholderResult> CreateFilePlaceholders(IReadOnlyList<RemoteFilePlaceholderRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);
            var results = new List<RemoteFilePlaceholderResult>(requests.Count);
            foreach (RemoteFilePlaceholderRequest request in requests)
            {
                results.Add(CreateFilePlaceholder(request));
            }

            return results;
        }

        void UnregisterSyncRoot(SyncPairSettings syncPair);

        void CreateDirectoryPlaceholder(RemoteDirectoryMaterializationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!Guid.TryParse(request.SyncPairId, out Guid syncPairId))
            {
                throw new ArgumentException("Virtual-files directory placeholder request contains an invalid sync pair id.", nameof(request));
            }

            SetInSyncState(
                new SyncPairSettings
                {
                    Id = syncPairId,
                    DisplayName = "Cotton Sync",
                    LocalRootPath = request.LocalRootPath,
                    RemoteDisplayPath = "/",
                    RemoteRootNodeId = request.RemoteRootNodeId,
                    Mode = SyncPairMode.WindowsVirtualFiles,
                    IsEnabled = true,
                },
                request.RelativePath);
        }

        void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath);

        void SetInSyncState(SyncPairSettings syncPair, string relativePath);

        void SetSyncRootInSyncState(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
        }

        WindowsCloudFilesPlaceholderState GetPlaceholderState(SyncPairSettings syncPair, string? relativePath = null)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            throw new NotSupportedException("Cloud Files placeholder state inspection is not supported by this adapter.");
        }

        WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler);

        void TransferData(WindowsCloudFilesTransferData transfer);
    }
}
