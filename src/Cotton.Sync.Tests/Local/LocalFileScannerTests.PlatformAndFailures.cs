// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text;
using Cotton.Sync;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public partial class LocalFileScannerTests
    {
        [Test]
        public void ScanPathMetadataLookupsAsync_RejectsUnsupportedFileReparsePoint()
        {
            WriteFile("target.txt", "target");
            string linkPath = FullPath("target-link.txt");
            TryCreateFileSymlink(linkPath, FullPath("target.txt"));
            LocalFileScanner scanner = new();

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(() =>
                scanner.ScanPathMetadataLookupsAsync(
                    _root,
                    ["target-link.txt"],
                    progress: null,
                    includeDirectoryDescendants: false));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("target-link.txt"));
                Assert.That(exception?.Reason, Does.Contain("unsupported file reparse point"));
            });
        }

        [Test]
        public void IsCloudFilesReparseTag_RecognizesWindowsCloudFilesFamilyOnly()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LocalFileScanner.IsCloudFilesReparseTag(0x9000401A), Is.True);
                Assert.That(LocalFileScanner.IsCloudFilesReparseTag(0x9000601A), Is.True);
                Assert.That(LocalFileScanner.IsCloudFilesReparseTag(0xA000000C), Is.False);
                Assert.That(LocalFileScanner.IsCloudFilesReparseTag(0x80000017), Is.False);
            });
        }

        [Test]
        public void IsCloudFilesOnlineOnlyAttributes_RecognizesRecallAndOfflineAttributes()
        {
            const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
            const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;

            Assert.Multiple(() =>
            {
                Assert.That(LocalFileScanner.IsCloudFilesOnlineOnlyAttributes(recallOnOpen), Is.True);
                Assert.That(LocalFileScanner.IsCloudFilesOnlineOnlyAttributes(recallOnDataAccess), Is.True);
                Assert.That(LocalFileScanner.IsCloudFilesOnlineOnlyAttributes(FileAttributes.Offline), Is.True);
                Assert.That(LocalFileScanner.IsCloudFilesOnlineOnlyAttributes(FileAttributes.ReparsePoint), Is.False);
            });
        }

        [Test]
        public void ShouldIncludeScopedDirectory_AllowsCloudFilesReparsePointsOnly()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    LocalFileScanner.ShouldIncludeScopedDirectory(
                        FileAttributes.Directory | FileAttributes.ReparsePoint,
                        isCloudFilesPlaceholder: true),
                    Is.True);
                Assert.That(
                    LocalFileScanner.ShouldIncludeScopedDirectory(
                        FileAttributes.Directory | FileAttributes.ReparsePoint,
                        isCloudFilesPlaceholder: false),
                    Is.False);
                Assert.That(
                    LocalFileScanner.ShouldIncludeScopedDirectory(
                        FileAttributes.Directory,
                        isCloudFilesPlaceholder: false),
                    Is.True);
            });
        }

        [Test]
        public async Task ScanAsync_ThrowsForLockedFile()
        {
            WriteFile("keep.txt", "keep");
            WriteFile("locked.txt", "locked");
            await using FileStream locked = new(
                FullPath("locked.txt"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            LocalFileScanner scanner = new LocalFileScanner();

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(() => scanner.ScanAsync(_root));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.RelativePath, Is.EqualTo("locked.txt"));
                Assert.That(exception.FullPath, Is.EqualTo(FullPath("locked.txt")));
                Assert.That(exception.InnerException, Is.TypeOf<IOException>());
            });
        }

        [Test]
        public async Task ScanTreeMetadataAsync_ReportsDirectoryRemovedDuringScanAsUnavailable()
        {
            Directory.CreateDirectory(FullPath("moving"));
            WriteFile(Path.Combine("moving", "child.txt"), "child");
            LocalFileScanner scanner = new LocalFileScanner();
            RecordingProgress<LocalTreeScanProgress> progress = new RecordingProgress<LocalTreeScanProgress>(item =>
            {
                if (item.CurrentPath == "moving" && Directory.Exists(FullPath("moving")))
                {
                    Directory.Delete(FullPath("moving"), recursive: true);
                }
            });

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => scanner.ScanTreeMetadataAsync(_root, progress));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.RelativePath, Is.EqualTo("moving"));
                Assert.That(exception.FullPath, Is.EqualTo(FullPath("moving")));
                Assert.That(exception.InnerException, Is.TypeOf<DirectoryNotFoundException>().Or.TypeOf<IOException>());
            });
        }

        [Test]
        public async Task ScanAsync_ThrowsForUnreadableUnixFile()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes are not available on this platform.");
                return;
            }

            WriteFile("unreadable.txt", "secret");
            string path = FullPath("unreadable.txt");
            UnixFileMode originalMode = File.GetUnixFileMode(path);
            LocalFileScanner scanner = new LocalFileScanner();

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.None);

                LocalFilePermissionDeniedException? exception = Assert.ThrowsAsync<LocalFilePermissionDeniedException>(() => scanner.ScanAsync(_root));

                Assert.Multiple(() =>
                {
                    Assert.That(exception, Is.Not.Null);
                    Assert.That(exception!.RelativePath, Is.EqualTo("unreadable.txt"));
                    Assert.That(exception.FullPath, Is.EqualTo(path));
                    Assert.That(exception.Reason, Does.Contain("Unix read permission"));
                });
            }
            finally
            {
                File.SetUnixFileMode(path, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
