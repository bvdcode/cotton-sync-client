// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesTransferData(
        WindowsCloudFilesConnectionKey ConnectionKey,
        WindowsCloudFilesTransferKey TransferKey,
        WindowsCloudFilesRequestKey RequestKey,
        byte[] Buffer,
        long Offset,
        long Length,
        int CompletionStatus)
    {
        public const int StatusSuccess = 0;
        public const int StatusUnsuccessful = unchecked((int)0xC0000001);

        public static WindowsCloudFilesTransferData Success(
            WindowsCloudFilesFetchDataRequest request,
            byte[] buffer,
            long offset,
            long length)
        {
            return new WindowsCloudFilesTransferData(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                buffer,
                offset,
                length,
                StatusSuccess);
        }

        public static WindowsCloudFilesTransferData Failure(WindowsCloudFilesFetchDataRequest request)
        {
            long length = request.RequiredLength < 0 ? 0 : request.RequiredLength;
            return new WindowsCloudFilesTransferData(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                [],
                request.RequiredOffset,
                length,
                StatusUnsuccessful);
        }
    }
}
