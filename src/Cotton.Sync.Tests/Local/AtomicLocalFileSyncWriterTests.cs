// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public class AtomicLocalFileSyncWriterTests
    {
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-local-writer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public async Task WriteFileAsync_RemovesTemporaryFileWhenDownloadFailsAndPreservesExistingFile()
        {
            string relativePath = "Docs/file.txt";
            WriteFile(relativePath, "existing");
            var writer = new AtomicLocalFileSyncWriter();

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await writer.WriteFileAsync(
                    _root,
                    relativePath,
                    async (stream, cancellationToken) =>
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
                        throw new InvalidOperationException("download failed");
                    }));

            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(ReadFile(relativePath), Is.EqualTo("existing"));
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
            });
        }

        [Test]
        public async Task WriteFileAsync_RemovesStaleTemporaryDownloadsBeforeWriting()
        {
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Directory.CreateDirectory(temporaryDirectory);
            string staleDownload = Path.Combine(temporaryDirectory, "stale.download");
            File.WriteAllText(staleDownload, "partial", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var writer = new AtomicLocalFileSyncWriter();

            await writer.WriteFileAsync(
                _root,
                "Docs/file.txt",
                async (stream, cancellationToken) =>
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("complete"), cancellationToken));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(staleDownload), Is.False);
                Assert.That(ReadFile("Docs/file.txt"), Is.EqualTo("complete"));
                Assert.That(Directory.GetFiles(temporaryDirectory, "*.download", SearchOption.AllDirectories), Is.Empty);
            });
        }

        [Test]
        public async Task DeleteFileAsync_MovesFileToDeletedQuarantineAndPreservesParentDirectory()
        {
            string relativePath = "Docs/file.txt";
            WriteFile(relativePath, "deleted-content");
            var writer = new AtomicLocalFileSyncWriter();

            await writer.DeleteFileAsync(_root, relativePath);

            string[] deletedFiles = Directory.GetFiles(
                Path.Combine(_root, ".cotton-sync", "deleted"),
                "file.txt",
                SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(FullPath(relativePath)), Is.False);
                Assert.That(deletedFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(deletedFiles[0]), Is.EqualTo("deleted-content"));
                Assert.That(Directory.Exists(Path.Combine(_root, "Docs")), Is.True);
            });
        }

        [Test]
        public async Task DeleteFileAsync_IsIdempotentOnlyWhenTargetIsConfirmedMissing()
        {
            AtomicLocalFileSyncWriter writer = new();

            await writer.DeleteFileAsync(_root, "Docs/missing.txt");

            Assert.That(Directory.Exists(Path.Combine(_root, ".cotton-sync", "deleted")), Is.False);
        }

        [Test]
        public void DeleteFileAsync_RejectsDirectoryTarget()
        {
            Directory.CreateDirectory(FullPath("Docs/Folder"));
            AtomicLocalFileSyncWriter writer = new();

            Assert.ThrowsAsync<IOException>(() => writer.DeleteFileAsync(_root, "Docs/Folder"));
            Assert.That(Directory.Exists(FullPath("Docs/Folder")), Is.True);
        }

        [Test]
        public async Task CreateDirectoryAsync_CreatesLocalDirectory()
        {
            var writer = new AtomicLocalFileSyncWriter();

            await writer.CreateDirectoryAsync(_root, "Docs/Empty");

            Assert.That(Directory.Exists(FullPath("Docs/Empty")), Is.True);
        }

        [Test]
        public async Task MoveDirectoryAsync_MovesCompleteSubtreeWithoutChangingContent()
        {
            WriteFile("Projects/Source/file.txt", "move-content");
            Directory.CreateDirectory(FullPath("Archive"));
            var writer = new AtomicLocalFileSyncWriter();

            await writer.MoveDirectoryAsync(_root, "Projects", "Archive/ProjectsRenamed");

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(FullPath("Projects")), Is.False);
                Assert.That(ReadFile("Archive/ProjectsRenamed/Source/file.txt"), Is.EqualTo("move-content"));
            });
        }

        [Test]
        public async Task MoveDirectoryAsync_RenamesDirectoryWhenOnlyCasingChanges()
        {
            WriteFile("Projects/file.txt", "case-content");
            var writer = new AtomicLocalFileSyncWriter();

            await writer.MoveDirectoryAsync(_root, "Projects", "projects");

            string renamedDirectory = Directory.EnumerateDirectories(_root).Single();
            Assert.Multiple(() =>
            {
                Assert.That(Path.GetFileName(renamedDirectory), Is.EqualTo("projects"));
                Assert.That(ReadFile("projects/file.txt"), Is.EqualTo("case-content"));
            });
        }

        [Test]
        public void MoveDirectoryAsync_WhenTargetExistsLeavesBothTreesUnchanged()
        {
            WriteFile("Source/source.txt", "source-content");
            WriteFile("Target/target.txt", "target-content");
            var writer = new AtomicLocalFileSyncWriter();

            Assert.That(
                async () => await writer.MoveDirectoryAsync(_root, "Source", "Target"),
                Throws.TypeOf<IOException>());
            Assert.Multiple(() =>
            {
                Assert.That(ReadFile("Source/source.txt"), Is.EqualTo("source-content"));
                Assert.That(ReadFile("Target/target.txt"), Is.EqualTo("target-content"));
            });
        }

        [Test]
        public async Task DeleteDirectoryAsync_MovesEmptyDirectoryToDeletedQuarantine()
        {
            Directory.CreateDirectory(FullPath("Docs/Empty"));
            var writer = new AtomicLocalFileSyncWriter();

            await writer.DeleteDirectoryAsync(_root, "Docs/Empty");

            string[] deletedDirectories = Directory.GetDirectories(
                Path.Combine(_root, ".cotton-sync", "deleted"),
                "Empty",
                SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(FullPath("Docs/Empty")), Is.False);
                Assert.That(deletedDirectories, Has.Length.EqualTo(1));
                Assert.That(Directory.Exists(FullPath("Docs")), Is.True);
            });
        }

        [Test]
        public async Task DeleteDirectoryAsync_MovesNonEmptyDirectoryToDeletedQuarantine()
        {
            WriteFile("Docs/NotEmpty/file.txt", "keep");
            var writer = new AtomicLocalFileSyncWriter();

            await writer.DeleteDirectoryAsync(_root, "Docs/NotEmpty");

            string[] deletedFiles = Directory.GetFiles(
                Path.Combine(_root, ".cotton-sync", "deleted"),
                "file.txt",
                SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(FullPath("Docs/NotEmpty")), Is.False);
                Assert.That(deletedFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(deletedFiles[0]), Is.EqualTo("keep"));
                Assert.That(Directory.Exists(FullPath("Docs")), Is.True);
            });
        }

        [Test]
        public void DeleteDirectoryAsync_RejectsFileTarget()
        {
            WriteFile("Docs/file.txt", "keep");
            AtomicLocalFileSyncWriter writer = new();

            Assert.ThrowsAsync<IOException>(() => writer.DeleteDirectoryAsync(_root, "Docs/file.txt"));
            Assert.That(File.Exists(FullPath("Docs/file.txt")), Is.True);
        }

        [Test]
        public async Task PayloadOperations_RejectIgnoredPaths()
        {
            var writer = new AtomicLocalFileSyncWriter();
            const string ignoredFilePath = ".cotton-sync/payload.txt";
            const string ignoredDirectoryPath = ".cotton-sync/payload";

            Assert.Multiple(() =>
            {
                Assert.That(
                    async () => await writer.WriteFileAsync(
                        _root,
                        ignoredFilePath,
                        async (stream, cancellationToken) =>
                            await stream.WriteAsync(Encoding.UTF8.GetBytes("payload"), cancellationToken)),
                    Throws.ArgumentException);
                Assert.That(
                    async () => await writer.DeleteFileAsync(_root, ignoredFilePath),
                    Throws.ArgumentException);
                Assert.That(
                    async () => await writer.CreateDirectoryAsync(_root, ignoredDirectoryPath),
                    Throws.ArgumentException);
                Assert.That(
                    async () => await writer.MoveDirectoryAsync(_root, ignoredDirectoryPath, "payload"),
                    Throws.ArgumentException);
                Assert.That(
                    async () => await writer.DeleteDirectoryAsync(_root, ignoredDirectoryPath),
                    Throws.ArgumentException);
                Assert.That(
                    () => writer.CreateConflictRelativePath(_root, ignoredFilePath, DateTime.UtcNow),
                    Throws.ArgumentException);
                Assert.That(Directory.Exists(Path.Combine(_root, ".cotton-sync")), Is.False);
            });
        }

        [Test]
        public void CreateConflictRelativePath_UsesIndexedSuffixWhenTimestampNameExists()
        {
            var writer = new AtomicLocalFileSyncWriter();
            DateTime timestamp = new(2026, 6, 3, 12, 30, 0, DateTimeKind.Utc);
            string firstConflictPath = writer.CreateConflictRelativePath(_root, "Docs/file.txt", timestamp);
            WriteFile(firstConflictPath, "first conflict");

            string secondConflictPath = writer.CreateConflictRelativePath(_root, "Docs/file.txt", timestamp);

            Assert.Multiple(() =>
            {
                Assert.That(firstConflictPath, Is.EqualTo("Docs/file (Cotton conflict 20260603T123000Z).txt"));
                Assert.That(secondConflictPath, Is.EqualTo("Docs/file (Cotton conflict 20260603T123000Z-2).txt"));
                Assert.That(File.Exists(FullPath(firstConflictPath)), Is.True);
                Assert.That(File.Exists(FullPath(secondConflictPath)), Is.False);
            });
        }

        [Test]
        public void CreateConflictRelativePath_SkipsExistingDirectoryWithCandidateName()
        {
            var writer = new AtomicLocalFileSyncWriter();
            DateTime timestamp = new(2026, 6, 3, 12, 30, 0, DateTimeKind.Utc);
            Directory.CreateDirectory(FullPath("Docs/file (Cotton conflict 20260603T123000Z).txt"));

            string conflictPath = writer.CreateConflictRelativePath(_root, "Docs/file.txt", timestamp);

            Assert.That(conflictPath, Is.EqualTo("Docs/file (Cotton conflict 20260603T123000Z-2).txt"));
        }

        private string FullPath(string relativePath)
        {
            return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private string ReadFile(string relativePath)
        {
            return File.ReadAllText(FullPath(relativePath));
        }

        private void WriteFile(string relativePath, string content)
        {
            string fullPath = FullPath(relativePath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
