// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal class LiveSyncManifestBarrier
    {
        private readonly object _gate = new();
        private string? _path;
        private TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Guid remoteRootNodeId)
        {
            lock (_gate)
            {
                if (_path is not null)
                {
                    throw new InvalidOperationException("The manifest barrier is already armed.");
                }
                _path = $"/api/v1/layouts/nodes/{remoteRootNodeId:D}/children";
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
                if (_path is null || request.Method != HttpMethod.Get || !response.IsSuccessStatusCode
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
