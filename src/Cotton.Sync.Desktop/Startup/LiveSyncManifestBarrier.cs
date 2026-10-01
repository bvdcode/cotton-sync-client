// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal class LiveSyncManifestBarrier
    {
        private readonly object _gate = new();
        private string? _path;
        private HttpMethod _method = HttpMethod.Get;
        private TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Guid remoteRootNodeId)
        {
            ArmPath($"/api/v1/layouts/nodes/{remoteRootNodeId:D}/children", HttpMethod.Get);
        }

        public void ArmChangeFeed()
        {
            ArmPath("/api/v1/sync/changes", HttpMethod.Get);
        }

        public void ArmRename(Guid remoteFileId)
        {
            ArmPath($"/api/v1/files/{remoteFileId:D}/rename", HttpMethod.Patch);
        }

        private void ArmPath(string path, HttpMethod method)
        {
            lock (_gate)
            {
                if (_path is not null)
                {
                    throw new InvalidOperationException("The manifest barrier is already armed.");
                }
                _path = path;
                _method = method;
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitUntilBlockedAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return _blocked.Task.WaitAsync(timeout, cancellationToken);
            }
        }

        public async Task HoldResponseAsync(
            HttpRequestMessage request,
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource blocked;
            Task released;
            lock (_gate)
            {
                if (_path is null || request.Method != _method || !response.IsSuccessStatusCode
                    || !string.Equals(request.RequestUri?.AbsolutePath, _path, StringComparison.Ordinal))
                {
                    return;
                }
                blocked = _blocked;
                released = _released.Task;
            }
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            blocked.TrySetResult();
            await released.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Release()
        {
            lock (_gate)
            {
                _path = null;
                _released.TrySetResult();
            }
        }
    }
}
