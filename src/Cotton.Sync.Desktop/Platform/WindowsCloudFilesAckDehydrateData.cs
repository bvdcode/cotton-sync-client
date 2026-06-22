// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesAckDehydrateData(
        WindowsCloudFilesConnectionKey ConnectionKey,
        WindowsCloudFilesTransferKey TransferKey,
        WindowsCloudFilesRequestKey RequestKey,
        byte[] FileIdentity,
        int CompletionStatus)
    {
        public const int StatusSuccess = 0;
        public const int StatusUnsuccessful = unchecked((int)0xC0000001);

        public static WindowsCloudFilesAckDehydrateData Success(WindowsCloudFilesDehydrateRequest request)
        {
            return new WindowsCloudFilesAckDehydrateData(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                request.FileIdentity.ToArray(),
                StatusSuccess);
        }

        public static WindowsCloudFilesAckDehydrateData Failure(WindowsCloudFilesDehydrateRequest request)
        {
            return new WindowsCloudFilesAckDehydrateData(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                request.FileIdentity.ToArray(),
                StatusUnsuccessful);
        }
    }
}
