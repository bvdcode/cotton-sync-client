// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Sync.Local;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsLocalProviderFileMarkerTests
    {
        private string _rootPath = string.Empty;
        private string _markerPath = string.Empty;

        [SetUp]
        public void SetUp()
        {
            string testRoot = Path.Combine(
                Path.GetTempPath(),
                "cotton-provider-file-marker-" + Guid.NewGuid().ToString("N"));
            _rootPath = Path.Combine(testRoot, "root");
            _markerPath = Path.Combine(testRoot, "markers");
            Directory.CreateDirectory(_rootPath);
        }

        [TearDown]
        public void TearDown()
        {
            string? testRoot = Directory.GetParent(_rootPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(testRoot) && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        [Test]
        public async Task IsUnchangedAsync_SuppressesOnlyTheMarkedFileWithOriginalContent()
        {
            Guid syncPairId = Guid.NewGuid();
            const string relativePath = "Docs/recovery.txt";
            string fullPath = WriteFile(relativePath, "remote recovery content");
            string hash = HashFile(fullPath);
            WindowsLocalProviderFileMarker marker = new WindowsLocalProviderFileMarker(_markerPath);

            await marker.MarkAsync(
                syncPairId,
                _rootPath,
                relativePath,
                hash,
                new FileInfo(fullPath).Length);

            bool unchanged = await marker.IsUnchangedAsync(
                syncPairId,
                _rootPath,
                Snapshot(relativePath, fullPath));
            File.WriteAllText(fullPath, "user changed recovery content");
            bool changed = await marker.IsUnchangedAsync(
                syncPairId,
                _rootPath,
                Snapshot(relativePath, fullPath));

            Assert.Multiple(() =>
            {
                Assert.That(unchanged, Is.True);
                Assert.That(changed, Is.False);
            });
        }

        [Test]
        public async Task IsUnchangedAsync_DoesNotSuppressAUserRename()
        {
            Guid syncPairId = Guid.NewGuid();
            const string sourcePath = "Docs/recovery.txt";
            const string targetPath = "Docs/renamed-recovery.txt";
            string sourceFullPath = WriteFile(sourcePath, "remote recovery content");
            WindowsLocalProviderFileMarker marker = new WindowsLocalProviderFileMarker(_markerPath);
            await marker.MarkAsync(
                syncPairId,
                _rootPath,
                sourcePath,
                HashFile(sourceFullPath),
                new FileInfo(sourceFullPath).Length);
            string targetFullPath = Path.Combine(_rootPath, targetPath.Replace('/', Path.DirectorySeparatorChar));
            File.Move(sourceFullPath, targetFullPath);

            bool unchanged = await marker.IsUnchangedAsync(
                syncPairId,
                _rootPath,
                Snapshot(targetPath, targetFullPath));

            Assert.That(unchanged, Is.False);
        }

        [Test]
        public async Task IsUnchangedAsync_DoesNotSuppressARecreatedFileAtTheSamePath()
        {
            Guid syncPairId = Guid.NewGuid();
            const string relativePath = "Docs/recovery.txt";
            string fullPath = WriteFile(relativePath, "remote recovery content");
            string hash = HashFile(fullPath);
            WindowsLocalProviderFileMarker marker = new WindowsLocalProviderFileMarker(_markerPath);
            await marker.MarkAsync(
                syncPairId,
                _rootPath,
                relativePath,
                hash,
                new FileInfo(fullPath).Length);
            File.Delete(fullPath);
            File.WriteAllText(fullPath, "remote recovery content");

            bool unchanged = await marker.IsUnchangedAsync(
                syncPairId,
                _rootPath,
                Snapshot(relativePath, fullPath));

            Assert.That(unchanged, Is.False);
        }

        private string WriteFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        private static LocalFileSnapshot Snapshot(string relativePath, string fullPath)
        {
            FileInfo info = new FileInfo(fullPath);
            return new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            };
        }

        private static string HashFile(string fullPath)
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));
        }
    }
}
