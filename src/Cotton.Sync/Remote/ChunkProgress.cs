// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal class ChunkProgress(IProgress<long> _progress, long _completed, long _chunkLength) : IProgress<long>
    {
        public void Report(long value)
        {
            _progress.Report(_completed + Math.Clamp(value, 0, _chunkLength));
        }
    }
}
