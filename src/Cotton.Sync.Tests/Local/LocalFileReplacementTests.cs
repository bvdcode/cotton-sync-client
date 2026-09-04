// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public class LocalFileReplacementTests
    {
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-local-replacement", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task WriteFileAsync_AppliesPlatformRetentionAndPreservesRemoteTimestamp()
        {
            const string relativePath = "file.txt";
            string targetPath = Path.Combine(_root, relativePath);
            await File.WriteAllTextAsync(targetPath, "original");
            DateTime remoteLastWrite = new(2026, 8, 3, 20, 0, 0, DateTimeKind.Utc);
            AtomicLocalFileSyncWriter writer = new();

            LocalFileWriteResult result = await writer.WriteFileAsync(
                _root,
                relativePath,
                WriteRemoteContentAsync,
                remoteLastWrite,
                Hash("original"));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("remote"));
                Assert.That(File.GetLastWriteTimeUtc(targetPath), Is.EqualTo(remoteLastWrite));
                Assert.That(result.ConflictRelativePath, Is.Null);
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });

            string preservationDirectory = Path.Combine(_root, ".cotton-sync", "deleted");
            if (OperatingSystem.IsWindows())
            {
                Assert.That(Directory.GetFileSystemEntries(preservationDirectory), Is.Empty);
            }
            else
            {
                string[] previousFiles = Directory.GetFiles(preservationDirectory, relativePath, SearchOption.AllDirectories);
                Assert.That(previousFiles.Select(File.ReadAllText), Is.EqualTo(new[] { "original" }));
            }
        }

        [Test]
        public async Task WriteFileAsync_CleansOnlyItsOwnEmptyRecoveryDirectories()
        {
            const string relativePath = "Docs/Nested/file.txt";
            string targetPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, "original");
            string retainedPath = await CreatePreviousVersionAsync();
            AtomicLocalFileSyncWriter writer = new();

            await writer.WriteFileAsync(
                _root,
                relativePath,
                WriteRemoteContentAsync,
                expectedLocalContentHash: Hash("original"));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(retainedPath), Is.EqualTo("local edit"));
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("remote"));
            });

            string[] retainedOperations = Directory.GetFileSystemEntries(Path.Combine(_root, ".cotton-sync", "deleted"));
            if (OperatingSystem.IsWindows())
            {
                Assert.That(retainedOperations, Has.Length.EqualTo(1));
            }
            else
            {
                Assert.That(retainedOperations, Has.Length.EqualTo(2));
            }
        }

        [Test]
        [Platform("Linux")]
        public async Task WriteFileAsync_RetainsLateNativeWriterBytesAfterMatchingReplacement()
        {
            const string relativePath = "Docs/Nested/file.txt";
            string targetPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, "original");
            ProcessStartInfo startInfo = new("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exec 3<> \"$1\" || exit 1; printf 'ready\\n'; IFS= read -r command || exit 2; printf 'late local edit' >&3; exec 3>&-");
            startInfo.ArgumentList.Add("writer");
            startInfo.ArgumentList.Add(targetPath);
            using Process nativeWriter = new() { StartInfo = startInfo };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            Assert.That(nativeWriter.Start(), Is.True);
            try
            {
                Assert.That(await nativeWriter.StandardOutput.ReadLineAsync(timeout.Token), Is.EqualTo("ready"));
                AtomicLocalFileSyncWriter writer = new();
                LocalFileWriteResult result = await writer.WriteFileAsync(
                    _root, relativePath, WriteRemoteContentAsync,
                    expectedLocalContentHash: Hash("original"), cancellationToken: timeout.Token);
                await nativeWriter.StandardInput.WriteLineAsync("continue");
                await nativeWriter.StandardInput.FlushAsync(timeout.Token);
                await nativeWriter.WaitForExitAsync(timeout.Token);
                string error = await nativeWriter.StandardError.ReadToEndAsync(timeout.Token);
                string[] previousFiles = Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "deleted"), "file.txt", SearchOption.AllDirectories);

                Assert.Multiple(() =>
                {
                    Assert.That(nativeWriter.ExitCode, Is.Zero, error);
                    Assert.That(result.ConflictRelativePath, Is.Null);
                    Assert.That(File.ReadAllText(targetPath), Is.EqualTo("remote"));
                    Assert.That(previousFiles.Select(File.ReadAllText), Is.EqualTo(new[] { "late local edit" }));
                    Assert.That(previousFiles.Single(), Does.EndWith(relativePath));
                });
            }
            finally
            {
                if (!nativeWriter.HasExited)
                {
                    nativeWriter.Kill();
                    await nativeWriter.WaitForExitAsync();
                }
            }
        }

        [Test]
        [Platform("Linux")]
        public async Task WriteFileAsync_DefersReplacementWhenTargetBecomesDirectoryDuringDownload()
        {
            const string relativePath = "file.txt";
            string targetPath = Path.Combine(_root, relativePath);
            await File.WriteAllTextAsync(targetPath, "original");
            AtomicLocalFileSyncWriter writer = new();

            Assert.ThrowsAsync<LocalFileUnavailableException>(() => writer.WriteFileAsync(
                _root,
                relativePath,
                async (stream, cancellationToken) =>
                {
                    File.Delete(targetPath);
                    Directory.CreateDirectory(targetPath);
                    await File.WriteAllTextAsync(Path.Combine(targetPath, "nested.txt"), "local edit", cancellationToken);
                    await WriteRemoteContentAsync(stream, cancellationToken);
                },
                expectedLocalContentHash: Hash("original")));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(targetPath, "nested.txt")), Is.EqualTo("local edit"));
                Assert.That(Directory.GetFileSystemEntries(Path.Combine(_root, ".cotton-sync", "deleted")), Is.Empty);
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });
        }

        [Test]
        [Platform("Win")]
        public async Task WriteFileAsync_RetainsPreviousVersionWhenAnOpenWriterPreventsVerification()
        {
            const string relativePath = "Docs/Nested/file.txt";
            string targetPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, "local edit");
            AtomicLocalFileSyncWriter writer = new();
            LocalFileUnavailableException? exception;
            await using (FileStream activeEditor = new(targetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                    () => writer.WriteFileAsync(_root, relativePath, WriteRemoteContentAsync, expectedLocalContentHash: Hash("original")));
            }

            string[] previousFiles = Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "deleted"), "file.txt", SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("remote"));
                Assert.That(previousFiles.Select(File.ReadAllText), Is.EqualTo(new[] { "local edit" }));
                Assert.That(previousFiles.Single(), Does.EndWith(relativePath.Replace('/', Path.DirectorySeparatorChar)));
                Assert.That(exception!.Reason, Does.Contain("retained"));
            });
        }

        [Test]
        public async Task CompleteAsync_RetainsPreviousVersionWhenCanceledAfterReplacement()
        {
            string previousPath = await CreatePreviousVersionAsync();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(
                () => LocalFileReplacementFinalizer.CompleteAsync(
                    _root,
                    "file.txt",
                    previousPath,
                    Hash("original"),
                    () => "conflict.txt",
                    cancellation.Token));

            AtomicLocalFileSyncWriter writer = new();
            await writer.WriteFileAsync(_root, "another.txt", WriteRemoteContentAsync);
            Assert.That(await File.ReadAllTextAsync(previousPath), Is.EqualTo("local edit"));
        }

        [Test]
        public async Task CompleteAsync_PreservesBothVersionsWhenConflictPathAppearsBeforeMove()
        {
            string previousPath = await CreatePreviousVersionAsync();

            Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => LocalFileReplacementFinalizer.CompleteAsync(
                    _root,
                    "file.txt",
                    previousPath,
                    Hash("original"),
                    () =>
                    {
                        File.WriteAllText(Path.Combine(_root, "conflict.txt"), "another local file");
                        return "conflict.txt";
                    },
                    CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(previousPath), Is.EqualTo("local edit"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "conflict.txt")), Is.EqualTo("another local file"));
            });
        }

        [Test]
        [Platform("Win")]
        public async Task CompleteAsync_BlocksWritesUntilVerifiedPreviousVersionIsMoved()
        {
            string previousPath = await CreatePreviousVersionAsync();

            LocalFileWriteResult result = await LocalFileReplacementFinalizer.CompleteAsync(
                _root,
                "file.txt",
                previousPath,
                Hash("original"),
                () =>
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using FileStream competingWriter = new(
                            previousPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                    });
                    return "conflict.txt";
                },
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.ConflictRelativePath, Is.EqualTo("conflict.txt"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "conflict.txt")), Is.EqualTo("local edit"));
                Assert.That(File.Exists(previousPath), Is.False);
            });
        }

        private async Task<string> CreatePreviousVersionAsync()
        {
            string preservationDirectory = Path.Combine(_root, ".cotton-sync", "deleted", "interrupted", "Docs");
            Directory.CreateDirectory(preservationDirectory);
            string previousPath = Path.Combine(preservationDirectory, "file.txt");
            await File.WriteAllTextAsync(previousPath, "local edit");
            return previousPath;
        }

        private static async Task WriteRemoteContentAsync(Stream stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("remote"u8.ToArray(), cancellationToken);
        }

        private static string Hash(string content)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        }
    }
}
