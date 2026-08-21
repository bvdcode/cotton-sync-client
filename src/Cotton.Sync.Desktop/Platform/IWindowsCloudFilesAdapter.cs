// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesAdapter
    {
        RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request);

        RemoteFilePlaceholderResult RestoreMissingFilePlaceholder(
            SyncPairSettings syncPair,
            SyncStateEntry fileState)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(fileState);
            throw new NotSupportedException("Cloud Files missing-placeholder recovery is not supported by this adapter.");
        }

        IReadOnlyList<RemoteFilePlaceholderResult> CreateFilePlaceholders(IReadOnlyList<RemoteFilePlaceholderRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);
            List<RemoteFilePlaceholderResult> results = new(requests.Count);
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

        Task<bool> DehydratePlaceholderIfContentMatchesAsync(
            SyncPairSettings syncPair,
            string relativePath,
            string expectedContentHash,
            Action? contentValidated,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Atomic Cloud Files dehydration is not supported by this adapter.");
        }

        void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            SetInSyncState(syncPair, relativePath);
        }

        void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            SetInSyncState(syncPair, relativePath);
        }

        void SetInSyncState(SyncPairSettings syncPair, string relativePath);

        Task<RemoteFilePlaceholderResult> FinalizeUploadedFilePlaceholderAsync(
            SyncPairSettings syncPair,
            SyncStateEntry fileState,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Atomic Cloud Files upload finalization is not supported by this adapter.");
        }

        void SetSyncRootInSyncState(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
        }

        WindowsCloudFilesPlaceholderState GetPlaceholderState(SyncPairSettings syncPair, string? relativePath = null)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            throw new NotSupportedException("Cloud Files placeholder state inspection is not supported by this adapter.");
        }

        byte[] GetPlaceholderIdentity(SyncPairSettings syncPair, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            throw new NotSupportedException("Cloud Files placeholder identity inspection is not supported by this adapter.");
        }

        void UpdatePlaceholderIdentity(
            SyncPairSettings syncPair,
            string relativePath,
            byte[] placeholderIdentity)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentNullException.ThrowIfNull(placeholderIdentity);
            throw new NotSupportedException("Cloud Files placeholder identity updates are not supported by this adapter.");
        }

        WindowsCloudFilesConnection ConnectSyncRoot(
            SyncPairSettings syncPair,
            IWindowsCloudFilesCallbackHandler callbackHandler);

        void TransferData(WindowsCloudFilesTransferData transfer);
    }
}
