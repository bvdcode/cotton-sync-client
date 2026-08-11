// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class DesktopPowerShellFileReader
    {
        public static async Task<string> ReadAsync(
            string script,
            string filePath,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["COTTON_SYNC_EXTERNAL_READ_PATH"] = filePath;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the external file-read helper process.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await WaitForExitAsync(process, filePath, timeout, cancellationToken).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    "External file-read helper failed with exit code "
                    + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": "
                    + CleanSingleLine(error));
            }

            return output;
        }

        private static async Task WaitForExitAsync(
            Process process,
            string filePath,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            if (!timeout.HasValue)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout.Value);
            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw new TimeoutException(
                    "External file-read helper timed out after "
                    + timeout.Value.TotalSeconds.ToString(
                        "0",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds while reading "
                    + filePath
                    + ".");
            }
        }

        private static string CleanSingleLine(string message)
        {
            return message
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }
    }
}
