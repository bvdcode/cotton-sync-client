// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopTraceLogLevel
    {
        public const string EnvironmentVariableName = "COTTON_SYNC_DESKTOP_LOG_LEVEL";

        public static LogLevel ResolveMinimumLevel()
        {
            string? value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return LogLevel.Information;
            }

            return Enum.TryParse(value.Trim(), ignoreCase: true, out LogLevel level)
                ? level
                : LogLevel.Information;
        }
    }
}
