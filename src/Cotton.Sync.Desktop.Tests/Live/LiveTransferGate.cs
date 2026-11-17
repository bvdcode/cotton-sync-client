// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal class LiveTransferGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _blocked = NewCompletion();
        private TaskCompletionSource _release = NewCompletion();
        private string? _path;
        private int _started;
        private int _completed;
        private bool _offline;

        public void SetOffline(bool offline)
        {
            lock (_lock)
            {
                _offline = offline;
            }
        }

        public void Arm(string path)
        {
            lock (_lock)
            {
                _path = path;
                _started = 0;
                _completed = 0;
                _blocked = NewCompletion();
                _release = NewCompletion();
            }
        }

        public Task WaitUntilBlockedAsync(CancellationToken cancellationToken)
        {
            return _blocked.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        }

        public async Task HoldAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Task? wait = null;
            lock (_lock)
            {
                if (_offline)
                {
                    throw new HttpRequestException("The test transport is offline.");
                }
                if (_path is not null && request.RequestUri!.AbsolutePath.Contains(_path, StringComparison.Ordinal))
                {
                    _started++;
                    if (_started > 1)
                    {
                        if (_completed > 0)
                        {
                            _blocked.TrySetResult();
                        }
                        wait = _release.Task;
                    }
                }
            }
            if (wait is not null)
            {
                await wait.WaitAsync(cancellationToken);
            }
        }

        public void Completed(HttpRequestMessage request)
        {
            lock (_lock)
            {
                if (_path is not null && request.RequestUri!.AbsolutePath.Contains(_path, StringComparison.Ordinal))
                {
                    _completed++;
                    if (_started > 1)
                    {
                        _blocked.TrySetResult();
                    }
                }
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                _path = null;
                _release.TrySetResult();
            }
        }

        private static TaskCompletionSource NewCompletion()
        {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
