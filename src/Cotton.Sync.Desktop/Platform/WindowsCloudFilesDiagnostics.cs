// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesDiagnostics : IWindowsCloudFilesDiagnostics
    {
        private const int DefaultCapacity = 200;
        private readonly object _gate = new();
        private readonly Queue<WindowsCloudFilesDiagnosticEvent> _events = [];
        private readonly int _capacity;

        public WindowsCloudFilesDiagnostics(int capacity = DefaultCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        public static WindowsCloudFilesDiagnostics Shared { get; } = new();

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
            var item = new WindowsCloudFilesDiagnosticEvent(
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
