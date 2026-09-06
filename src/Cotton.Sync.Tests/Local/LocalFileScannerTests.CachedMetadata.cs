// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public partial class LocalFileScannerTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task CreateSnapshotAsync_RefreshesCachedMetadataBeforeReadingCurrentFile(bool computeHash)
        {
            const string relativePath = "cached.txt";
            WriteFile(relativePath, "old");
            FileInfo file = new(FullPath(relativePath));
            long originalSize = file.Length;
            DateTime originalWriteTime = file.LastWriteTimeUtc;
            DateTime updatedWriteTime = originalWriteTime.AddMinutes(3);
            const string updatedContent = "updated file contents";
            WriteFile(relativePath, updatedContent);
            File.SetLastWriteTimeUtc(file.FullName, updatedWriteTime);
            Assert.Multiple(() =>
            {
                Assert.That(file.Length, Is.EqualTo(originalSize));
                Assert.That(file.LastWriteTimeUtc, Is.EqualTo(originalWriteTime));
            });

            LocalFileSnapshot snapshot = await LocalFileSnapshotFactory.CreateAsync(
                file,
                relativePath,
                computeHash,
                isCloudFilesPlaceholder: false,
                isCloudFilesOnlineOnlyPlaceholder: false,
                CancellationToken.None);

            string expectedHash = computeHash ? Hash(updatedContent) : string.Empty;
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.RelativePath, Is.EqualTo(relativePath));
                Assert.That(snapshot.FullPath, Is.EqualTo(file.FullName));
                Assert.That(snapshot.SizeBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(updatedContent)));
                Assert.That(snapshot.LastWriteUtc, Is.EqualTo(updatedWriteTime));
                Assert.That(snapshot.ContentHash, Is.EqualTo(expectedHash));
            });
        }
    }
}
