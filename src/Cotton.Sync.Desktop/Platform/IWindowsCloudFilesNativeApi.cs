// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesNativeApi
    {
        void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration);

        void UnregisterSyncRoot(string localRootPath);

        void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder);

        void CreatePlaceholders(IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
        {
            ArgumentNullException.ThrowIfNull(placeholders);
            foreach (WindowsCloudFilesNativePlaceholder placeholder in placeholders)
            {
                CreatePlaceholder(placeholder);
            }
        }

        void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder);

        void ConvertToPlaceholder(string filePath, byte[] fileIdentity, bool isDirectory, bool markInSync)
        {
            throw new NotSupportedException("Cloud Files placeholder conversion is not supported by this native API.");
        }

        void SetPinState(string filePath, WindowsCloudFilesPinState pinState);

        void SetInSyncState(string filePath);

        WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath);

        void HydratePlaceholder(string filePath);

        WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request);

        void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey);

        void TransferData(WindowsCloudFilesTransferData transfer);

        void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate);

        void DehydratePlaceholder(string filePath);
    }
}
