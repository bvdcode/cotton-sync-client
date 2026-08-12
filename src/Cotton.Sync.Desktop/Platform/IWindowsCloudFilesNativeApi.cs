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

        Task<WindowsCloudFilesUploadedFileFinalizationResult> FinalizeUploadedFileAsync(
            WindowsCloudFilesUploadedFileFinalizationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Atomic Cloud Files upload finalization is not supported by this native API.");
        }

        void ConvertToPlaceholder(string filePath, byte[] fileIdentity, bool isDirectory, bool markInSync)
        {
            throw new NotSupportedException("Cloud Files placeholder conversion is not supported by this native API.");
        }

        void SetPinState(string filePath, WindowsCloudFilesPinState pinState);

        void SetInSyncState(string filePath);

        WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath);

        byte[] GetPlaceholderIdentity(string filePath)
        {
            throw new NotSupportedException("Cloud Files placeholder identity inspection is not supported by this native API.");
        }

        void UpdatePlaceholderIdentity(string filePath, byte[] placeholderIdentity)
        {
            throw new NotSupportedException("Cloud Files placeholder identity updates are not supported by this native API.");
        }

        void HydratePlaceholder(string filePath);

        WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request);

        void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey);

        void TransferData(WindowsCloudFilesTransferData transfer);

        void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate);

        void DehydratePlaceholder(string filePath);

        Task<bool> DehydratePlaceholderIfContentMatchesAsync(
            string filePath,
            string expectedContentHash,
            Action? contentValidated,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Atomic Cloud Files dehydration is not supported by this native API.");
        }
    }
}
