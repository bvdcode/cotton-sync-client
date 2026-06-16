// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesConnection : IDisposable
    {
        private readonly Action<WindowsCloudFilesConnectionKey> _disconnect;
        private readonly IDisposable? _lifetime;
        private int _disposed;

        public WindowsCloudFilesConnection(
            string localRootPath,
            WindowsCloudFilesConnectionKey connectionKey,
            Action<WindowsCloudFilesConnectionKey> disconnect,
            IDisposable? lifetime = null)
        {
            LocalRootPath = string.IsNullOrWhiteSpace(localRootPath)
                ? throw new ArgumentException("Sync root path is required.", nameof(localRootPath))
                : localRootPath;
            ConnectionKey = connectionKey;
            _disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
            _lifetime = lifetime;
        }

        public string LocalRootPath { get; }

        public WindowsCloudFilesConnectionKey ConnectionKey { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _disconnect(ConnectionKey);
                }
                finally
                {
                    _lifetime?.Dispose();
                }
            }
        }
    }
}
