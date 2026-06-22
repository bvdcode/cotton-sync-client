// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal class DesktopTraceLoggerFactory : ILoggerFactory
    {
        private readonly LogLevel _minimumLevel;

        public DesktopTraceLoggerFactory()
            : this(DesktopTraceLogLevel.ResolveMinimumLevel())
        {
        }

        public DesktopTraceLoggerFactory(LogLevel minimumLevel)
        {
            _minimumLevel = minimumLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new DesktopTraceLogger(categoryName, _minimumLevel);
        }

        public void AddProvider(ILoggerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public void Dispose()
        {
        }
    }
}
