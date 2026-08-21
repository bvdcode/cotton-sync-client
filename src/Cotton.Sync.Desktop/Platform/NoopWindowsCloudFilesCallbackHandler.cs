// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class NoopWindowsCloudFilesCallbackHandler : IWindowsCloudFilesCallbackHandler
    {
        public static NoopWindowsCloudFilesCallbackHandler Instance { get; } = new();

        public Task HandleFetchDataAsync(
            WindowsCloudFilesFetchDataRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
        {
        }

        public Task HandleDehydrateAsync(
            WindowsCloudFilesDehydrateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
        {
        }
    }
}
