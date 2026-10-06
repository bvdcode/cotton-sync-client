// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Text;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        private static readonly TimeSpan CacheLeaseProcessTimeout = TimeSpan.FromSeconds(30);

        [Test]
        [Platform("Win")]
        public async Task DownloadFileAsync_WaitsForOtherProcessAndDoesNotPruneItsActiveCache()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            string root = Path.Combine(_root, "cache");
            string directory = Path.Combine(root, fileId.ToString("N") + "-" + Hash(content));
            string readyPath = Path.Combine(_root, "lease-ready");
            string releasePath = Path.Combine(_root, "lease-release");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "00000001.chunk"), content.AsMemory(4, 4).ToArray());
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-2));
            ProcessStartInfo startInfo = new("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$ErrorActionPreference='Stop'; "
                + "$rootLease=[System.IO.File]::Open($env:COTTON_TEST_ROOT_LOCK,'OpenOrCreate','Read','Read'); "
                + "$fileLease=[System.IO.File]::Open($env:COTTON_TEST_FILE_LOCK,'OpenOrCreate','ReadWrite','None'); "
                + "try { [System.IO.File]::WriteAllText($env:COTTON_TEST_READY,'ready'); "
                + "while(-not [System.IO.File]::Exists($env:COTTON_TEST_RELEASE)){Start-Sleep -Milliseconds 10} } "
                + "finally { $fileLease.Dispose(); $rootLease.Dispose() }");
            startInfo.Environment["COTTON_TEST_ROOT_LOCK"] = Path.Combine(root, "downloads.lock");
            startInfo.Environment["COTTON_TEST_FILE_LOCK"] = directory + ".lock";
            startInfo.Environment["COTTON_TEST_READY"] = readyPath;
            startInfo.Environment["COTTON_TEST_RELEASE"] = releasePath;
            using Process process = Process.Start(startInfo)!;
            Task<string> errorOutput = process.StandardError.ReadToEndAsync();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            await using MemoryStream destination = new();
            Task? download = null;
            try
            {
                await WaitForLeaseReadyAsync(process, readyPath, errorOutput);
                download = CreateDownloader(client).DownloadFileAsync(
                    new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                    "shared.bin", destination, null);
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                Assert.Multiple(() =>
                {
                    Assert.That(download.IsCompleted, Is.False);
                    Assert.That(client.FilesClient.ChunkDownloads, Is.Empty);
                    Assert.That(Directory.Exists(directory), Is.True);
                    Assert.That(File.ReadAllBytes(Path.Combine(directory, "00000001.chunk")),
                        Is.EqualTo(content.AsSpan(4, 4).ToArray()));
                });
            }
            finally
            {
                await File.WriteAllTextAsync(releasePath, "release");
                try
                {
                    await process.WaitForExitAsync().WaitAsync(CacheLeaseProcessTimeout);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                if (download is not null)
                {
                    await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            string errors = await errorOutput;
            _ = await standardOutput;
            Assert.Multiple(() =>
            {
                Assert.That(process.ExitCode, Is.Zero, errors);
                Assert.That(errors, Is.Empty);
                Assert.That(destination.ToArray(), Is.EqualTo(content));
                Assert.That(client.FilesClient.ChunkDownloads.Select(item => item.ChunkNumber),
                    Is.EquivalentTo(new[] { 0, 2 }));
                Assert.That(Directory.EnumerateDirectories(root), Is.Empty);
            });
        }

        private static async Task WaitForLeaseReadyAsync(Process process, string readyPath, Task<string> errorOutput)
        {
            long started = Stopwatch.GetTimestamp();
            while (!File.Exists(readyPath))
            {
                if (process.HasExited)
                {
                    string errors = await errorOutput;
                    throw new AssertionException(
                        $"The cache reservation process exited with code {process.ExitCode} before becoming ready: {errors}");
                }
                if (Stopwatch.GetElapsedTime(started) > CacheLeaseProcessTimeout)
                {
                    throw new AssertionException("The cache reservation process did not become ready within the startup timeout.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
    }
}
