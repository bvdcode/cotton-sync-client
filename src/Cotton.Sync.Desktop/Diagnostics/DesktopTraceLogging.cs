// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopTraceLogging
    {
        public static void Install(DesktopAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            Directory.CreateDirectory(paths.DataDirectory);
            WindowsCloudFilesDiagnostics.Shared.EnableFileLogging(paths.CloudFilesLogFilePath);
            if (Trace.Listeners
                .OfType<RotatingFileTraceListener>()
                .Any(listener => string.Equals(listener.Path, paths.LogFilePath, StringComparison.Ordinal)))
            {
                return;
            }

            Trace.Listeners.Add(new RotatingFileTraceListener(paths.LogFilePath, DesktopLogRetention.MaxFileSizeBytes));
            Trace.AutoFlush = true;
            Trace.TraceInformation("Cotton Sync desktop logging initialized.");
        }
    }
}
