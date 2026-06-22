// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal static class DesktopClipboardServiceFactory
    {
        private const string WindowsPowerShellCommandName = "powershell.exe";
        private const string PowerShellCoreCommandName = "pwsh.exe";

        public static IDesktopClipboardService CreateDefault()
        {
            return CreateForCurrentPlatform(Environment.GetEnvironmentVariable("PATH"));
        }

        internal static IDesktopClipboardService CreateForCurrentPlatform(string? pathValue)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new UnsupportedDesktopClipboardService();
            }

            string? powerShellPath = ResolveFirstExecutablePath(
                [WindowsPowerShellCommandName, PowerShellCoreCommandName],
                pathValue);
            return powerShellPath is null
                ? new UnsupportedDesktopClipboardService()
                : new WindowsClipboardService(powerShellPath);
        }

        private static string? ResolveFirstExecutablePath(
            IReadOnlyList<string> commandNames,
            string? pathValue)
        {
            foreach (string commandName in commandNames)
            {
                string? executablePath = ExecutablePathResolver.Resolve(commandName, pathValue);
                if (executablePath is not null)
                {
                    return executablePath;
                }
            }

            return null;
        }
    }
}
