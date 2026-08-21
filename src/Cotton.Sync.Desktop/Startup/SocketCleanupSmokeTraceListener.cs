// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Startup
{
    internal class SocketCleanupSmokeTraceListener : TraceListener
    {
        private readonly StringWriter _writer = new();

        public string Output => _writer.ToString();

        public override void Write(string? message)
        {
            _writer.Write(message);
        }

        public override void WriteLine(string? message)
        {
            _writer.WriteLine(message);
        }
    }
}
