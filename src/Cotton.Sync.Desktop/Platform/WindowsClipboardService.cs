// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Text;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsClipboardService : IDesktopClipboardService
    {
        private const int CopyTimeoutMilliseconds = 10_000;
        private readonly string _powerShellPath;

        public WindowsClipboardService(string powerShellPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(powerShellPath);
            _powerShellPath = powerShellPath.Trim();
        }

        public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CopyTimeoutMilliseconds);
            ProcessStartInfo startInfo = CreateStartInfo(_powerShellPath, text);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell could not be started.");
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string cleanError = error.Trim();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(cleanError)
                        ? "PowerShell clipboard command failed."
                        : "PowerShell clipboard command failed: " + cleanError);
            }
        }

        internal static ProcessStartInfo CreateStartInfo(string powerShellPath, string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(powerShellPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            ProcessStartInfo startInfo = new()
            {
                FileName = powerShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["COTTON_SYNC_CLIPBOARD_TEXT"] = text;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodePowerShellCommand(CreateClipboardCommand()));
            return startInfo;
        }

        internal static string DecodePowerShellCommand(string encodedCommand)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(encodedCommand);
            return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
        }

        private static string CreateClipboardCommand()
        {
            return string.Join(
                Environment.NewLine,
                [
                    "$ErrorActionPreference = 'Stop'",
                    "Set-Clipboard -Value $env:COTTON_SYNC_CLIPBOARD_TEXT",
                ]);
        }

        private static string EncodePowerShellCommand(string command)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }
    }
}
