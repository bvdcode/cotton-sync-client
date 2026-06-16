// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesAdapter
    {
        RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request);

        void UnregisterSyncRoot(SyncPairSettings syncPair);

        WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler);

        void TransferData(WindowsCloudFilesTransferData transfer);
    }
}
