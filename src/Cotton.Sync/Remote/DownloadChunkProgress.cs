// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal class DownloadChunkProgress : IProgress<long>
    {
        private readonly Action<long> _reportDelta;
        private readonly object _gate = new();
        private long _reportedBytes;

        public DownloadChunkProgress(Action<long> reportDelta)
        {
            _reportDelta = reportDelta;
        }

        public void Report(long value)
        {
            lock (_gate)
            {
                if (value <= _reportedBytes)
                {
                    return;
                }

                long delta = value - _reportedBytes;
                _reportedBytes = value;
                _reportDelta(delta);
            }
        }
    }
}
