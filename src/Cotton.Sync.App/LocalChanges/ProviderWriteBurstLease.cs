// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal class ProviderWriteBurstLease : IDisposable
    {
        private readonly Action<Guid> _endBurst;
        private readonly Guid _syncPairId;
        private int _disposed;

        public ProviderWriteBurstLease(Action<Guid> endBurst, Guid syncPairId)
        {
            _endBurst = endBurst ?? throw new ArgumentNullException(nameof(endBurst));
            _syncPairId = syncPairId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _endBurst(_syncPairId);
            }
        }
    }
}
