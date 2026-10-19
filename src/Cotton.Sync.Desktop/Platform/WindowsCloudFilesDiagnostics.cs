// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Diagnostics;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesDiagnostics : IWindowsCloudFilesDiagnostics
    {
        private const int DefaultCapacity = 200;
        private readonly object _gate = new();
        private readonly Queue<WindowsCloudFilesDiagnosticEvent> _events = [];
        private readonly int _capacity;
        private WindowsCloudFilesDiagnosticLog? _fileLog;

        public WindowsCloudFilesDiagnostics(int capacity = DefaultCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        public static WindowsCloudFilesDiagnostics Shared { get; } = new();

        public void EnableFileLogging(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            lock (_gate)
            {
                if (string.Equals(_fileLog?.Path, path, StringComparison.Ordinal))
                {
                    return;
                }

                _fileLog?.Dispose();
                _fileLog = new WindowsCloudFilesDiagnosticLog(path);
                foreach (WindowsCloudFilesDiagnosticEvent item in _events)
                {
                    _fileLog.Write(item);
                }
            }
        }

        public void Record(
            string operation,
            string status,
            string? syncPairId = null,
            string? localRootPath = null,
            string? relativePath = null,
            string? details = null,
            int? hResult = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentException.ThrowIfNullOrWhiteSpace(status);
            WindowsCloudFilesDiagnosticEvent item = new(
                DateTimeOffset.UtcNow,
                operation,
                status,
                syncPairId,
                localRootPath,
                relativePath,
                details,
                hResult);
            lock (_gate)
            {
                while (_events.Count >= _capacity)
                {
                    _events.Dequeue();
                }

                _events.Enqueue(item);
                _fileLog?.Write(item);
            }
        }

        public IReadOnlyList<WindowsCloudFilesDiagnosticEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}
