// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public class LocalFileDeletionFinalizerTests
    {
        private string _root = string.Empty;
        private string _target = string.Empty;
        private string _preserved = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-local-deletion", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _target = Path.Combine(_root, "file.txt");
            _preserved = Path.Combine(_root, "preserved.txt");
            File.WriteAllText(_target, "original");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task CompleteAsync_RestoresSameMetadataChangedContentAfterMove()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.Move(_target, _preserved);
            File.WriteAllText(_preserved, "modified");
            File.SetLastWriteTimeUtc(_preserved, expected.LastWriteUtc);

            Assert.ThrowsAsync<LocalFileUnavailableException>(() => CompleteAsync(expected));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_target), Is.EqualTo("modified"));
                Assert.That(File.Exists(_preserved), Is.False);
            });
        }

        [Test]
        public async Task CompleteAsync_CancellationAfterMoveRestoresOriginalPath()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.Move(_target, _preserved);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(() => CompleteAsync(expected, cancellation.Token));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_target), Is.EqualTo("original"));
                Assert.That(File.Exists(_preserved), Is.False);
            });
        }

        [Test]
        public async Task CompleteAsync_RestoreDoesNotOverwriteNewTarget()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.Move(_target, _preserved);
            File.WriteAllText(_preserved, "modified");
            File.SetLastWriteTimeUtc(_preserved, expected.LastWriteUtc);
            File.WriteAllText(_target, "new target");

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(() => CompleteAsync(expected));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_target), Is.EqualTo("new target"));
                Assert.That(File.ReadAllText(_preserved), Is.EqualTo("modified"));
                Assert.That(exception!.Reason, Does.Contain(_preserved));
            });
        }

        [Test]
        public async Task CompleteAsync_UnchangedFileRemainsInQuarantine()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.Move(_target, _preserved);

            await CompleteAsync(expected);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(_target), Is.False);
                Assert.That(File.ReadAllText(_preserved), Is.EqualTo("original"));
            });
        }

        [Test]
        public async Task DeleteFileAsync_RestoredContentLeavesNoEmptyQuarantineTree()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.WriteAllText(_target, "modified");
            File.SetLastWriteTimeUtc(_target, expected.LastWriteUtc);
            AtomicLocalFileSyncWriter writer = new();

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => writer.DeleteFileAsync(_root, "file.txt", expected));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_target), Is.EqualTo("modified"));
                Assert.That(exception!.FullPath, Is.EqualTo(_target));
                Assert.That(Directory.GetDirectories(Path.Combine(_root, ".cotton-sync", "deleted")), Is.Empty);
            });
        }

        [Test]
        public async Task CompleteAsync_ExpectedPlaceholderReplacedByOrdinaryFileIsRestored()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            expected.IsCloudFilesPlaceholder = true;
            expected.IsCloudFilesOnlineOnlyPlaceholder = true;
            expected.ContentHash = string.Empty;
            File.Move(_target, _preserved);

            Assert.ThrowsAsync<LocalFileUnavailableException>(() => CompleteAsync(expected));

            Assert.That(File.ReadAllText(_target), Is.EqualTo("original"));
        }

        [Test]
        public async Task DeleteFileAsync_AbsentSnapshotDoesNotDeleteAppearedFile()
        {
            AtomicLocalFileSyncWriter writer = new();

            Assert.ThrowsAsync<LocalFileUnavailableException>(() => writer.DeleteFileAsync(_root, "file.txt", expectedLocalFile: null));

            Assert.That(await File.ReadAllTextAsync(_target), Is.EqualTo("original"));
        }

        [Test]
        [Platform("Win")]
        public async Task CompleteAsync_OpenWriterPreventsVerificationAndRestoresFile()
        {
            LocalFileSnapshot expected = await SnapshotAsync();
            File.Move(_target, _preserved);
            await using (FileStream openWriter = new(_preserved, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.ThrowsAsync<LocalFileUnavailableException>(() => CompleteAsync(expected));
            }

            Assert.That(File.ReadAllText(_target), Is.EqualTo("original"));
        }

        private async Task<LocalFileSnapshot> SnapshotAsync()
        {
            IReadOnlyList<LocalFileSnapshot> files = await new LocalFileScanner().ScanAsync(_root);
            return files.Single();
        }

        private Task CompleteAsync(LocalFileSnapshot expected, CancellationToken cancellationToken = default)
        {
            return LocalFileDeletionFinalizer.CompleteAsync(_target, _preserved, "file.txt", expected, cancellationToken);
        }
    }
}
