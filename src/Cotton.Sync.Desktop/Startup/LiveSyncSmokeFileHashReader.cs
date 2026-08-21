// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class LiveSyncSmokeFileHashReader
    {
        private const string WindowsHashScript =
            "$ErrorActionPreference='Stop'; "
            + "$paths=[Console]::In.ReadToEnd() | ConvertFrom-Json; "
            + "$results=@(); "
            + "foreach($path in $paths){ "
            + "try { "
            + "$stream=[IO.File]::OpenRead([string]$path); "
            + "try { $sha=[Security.Cryptography.SHA256]::Create(); "
            + "try { $bytes=$sha.ComputeHash($stream) } finally { $sha.Dispose() } } "
            + "finally { $stream.Dispose() }; "
            + "$hash=([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant(); "
            + "$results+=@{Path=[string]$path;Sha256=$hash;Error=$null} "
            + "} catch { $results+=@{Path=[string]$path;Sha256=$null;Error=$_.Exception.Message} } "
            + "}; "
            + "ConvertTo-Json -InputObject $results -Compress";

        public static async Task<IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult>> ReadAsync(
            IEnumerable<string> filePaths,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(filePaths);
            string[] paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0)
            {
                return new Dictionary<string, LiveSyncSmokeFileHashReadResult>(StringComparer.OrdinalIgnoreCase);
            }

            if (!OperatingSystem.IsWindows())
            {
                return await ReadInProcessAsync(paths, cancellationToken).ConfigureAwait(false);
            }

            string input = JsonSerializer.Serialize(paths);
            string output = await RunWindowsReaderAsync(input, cancellationToken).ConfigureAwait(false);
            LiveSyncSmokeFileHashReadResult[]? reads = JsonSerializer.Deserialize<LiveSyncSmokeFileHashReadResult[]>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (reads is null)
            {
                throw new IOException("External file hash reader returned no results.");
            }

            return reads.ToDictionary(read => read.Path, StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult>> ReadInProcessAsync(
            IEnumerable<string> paths,
            CancellationToken cancellationToken)
        {
            Dictionary<string, LiveSyncSmokeFileHashReadResult> results = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                try
                {
                    await using FileStream stream = File.OpenRead(path);
                    byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                    results[path] = new LiveSyncSmokeFileHashReadResult(
                        path,
                        Convert.ToHexStringLower(hash),
                        null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    results[path] = new LiveSyncSmokeFileHashReadResult(path, null, exception.Message);
                }
            }

            return results;
        }

        private static async Task<string> RunWindowsReaderAsync(string input, CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(WindowsHashScript);

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the external file hash reader.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    "External file hash reader failed with exit code "
                    + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": "
                    + error.Replace('\r', ' ').Replace('\n', ' ').Trim());
            }

            return output;
        }
    }

}
