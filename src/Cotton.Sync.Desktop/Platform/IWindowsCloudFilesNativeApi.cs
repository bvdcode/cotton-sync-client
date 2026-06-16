// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesNativeApi
    {
        void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration);

        void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder);

        WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request);

        void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey);

        void TransferData(WindowsCloudFilesTransferData transfer);
    }
}
