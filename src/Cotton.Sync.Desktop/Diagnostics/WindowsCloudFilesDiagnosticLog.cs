// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal class WindowsCloudFilesDiagnosticLog(string path) : IDisposable
    {
        private readonly RotatingFileTraceListener _listener = new(path, DesktopLogRetention.MaxFileSizeBytes);

        public string Path => _listener.Path;

        public void Write(WindowsCloudFilesDiagnosticEvent item)
        {
            string hResult = item.HResult is int code ? $"0x{code:X8}" : "-";
            string message = FormattableString.Invariant(
                $"{item.TimestampUtc:O} operation={item.Operation}; status={item.Status}; pair={item.SyncPairId}; root={item.LocalRootPath}; path={item.RelativePath}; HRESULT={hResult}; details={item.Details}");
            string line = DesktopSecretRedactor.Redact(message)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            _listener.Write(line + Environment.NewLine);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
