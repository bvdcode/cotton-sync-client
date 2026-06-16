// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesCallbackHandler
    {
        Task HandleFetchDataAsync(
            WindowsCloudFilesFetchDataRequest request,
            CancellationToken cancellationToken = default);

        void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request);
    }
}
