// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class LiveSyncSmokeFileHashReaderTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-live-hash-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public async Task ReadAsync_ReturnsExactHashesAndPerFileErrorsInOneBatch()
        {
            string contentPath = Path.Combine(_tempDirectory, "content with spaces.bin");
            string emptyPath = Path.Combine(_tempDirectory, "empty.bin");
            string missingPath = Path.Combine(_tempDirectory, "missing.bin");
            byte[] content = [0, 1, 2, 127, 128, 255];
            await File.WriteAllBytesAsync(contentPath, content);
            await File.WriteAllBytesAsync(emptyPath, []);

            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> results =
                await LiveSyncSmokeFileHashReader.ReadAsync([contentPath, emptyPath, missingPath, contentPath]);

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(3));
                Assert.That(results[contentPath].Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))));
                Assert.That(results[contentPath].Error, Is.Null);
                Assert.That(results[emptyPath].Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData([]))));
                Assert.That(results[emptyPath].Error, Is.Null);
                Assert.That(results[missingPath].Sha256, Is.Null);
                Assert.That(results[missingPath].Error, Is.Not.Empty);
            });
        }

        [Test]
        public async Task ReadAsync_WithNoPathsReturnsEmptyResult()
        {
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> results =
                await LiveSyncSmokeFileHashReader.ReadAsync([]);

            Assert.That(results, Is.Empty);
        }

        [Test]
        public async Task ReadAsync_WithSinglePathReturnsArrayResult()
        {
            string filePath = Path.Combine(_tempDirectory, "single.bin");
            await File.WriteAllBytesAsync(filePath, [42]);

            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> results =
                await LiveSyncSmokeFileHashReader.ReadAsync([filePath]);

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[filePath].Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData([42]))));
                Assert.That(results[filePath].Error, Is.Null);
            });
        }
    }
}
