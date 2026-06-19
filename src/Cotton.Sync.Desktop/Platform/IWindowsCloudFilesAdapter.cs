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

        void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath);

        void SetInSyncState(SyncPairSettings syncPair, string relativePath);

        WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler);

        void TransferData(WindowsCloudFilesTransferData transfer);
    }
}
