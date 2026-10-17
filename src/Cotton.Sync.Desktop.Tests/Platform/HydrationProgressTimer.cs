// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class HydrationProgressTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        public bool IsDisposed => _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

        public void Tick()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
