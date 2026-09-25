// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal class DownloadTransferProgress : IProgress<long>
    {
        private readonly IProgress<SyncTransferProgress> _progress;
        private readonly string _relativePath;
        private readonly long? _totalBytes;
        private readonly object _gate = new();
        private long _lastTransferredBytes;

        public DownloadTransferProgress(
            IProgress<SyncTransferProgress> progress,
            string relativePath,
            long? totalBytes)
        {
            _progress = progress;
            _relativePath = relativePath;
            _totalBytes = totalBytes;
        }

        public long LastTransferredBytes
        {
            get
            {
                lock (_gate)
                {
                    return _lastTransferredBytes;
                }
            }
        }

        public void Report(long transferredBytes)
        {
            lock (_gate)
            {
                _lastTransferredBytes = Math.Max(_lastTransferredBytes, transferredBytes);
                _progress.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    _relativePath,
                    _lastTransferredBytes,
                    _totalBytes));
            }
        }
    }
}
