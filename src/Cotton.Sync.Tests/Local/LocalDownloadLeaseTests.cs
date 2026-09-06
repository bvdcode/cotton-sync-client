// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    public class LocalDownloadLeaseTests
    {
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-download-lease", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void SharedDownloads_PreventCleanupBeforePayloadCreationAndAfterPayloadClose()
        {
            string payload = Path.Combine(_root, "active.download");
            using (FileStream first = LocalDownloadLease.Acquire(_root, "first.txt", payload))
            using (FileStream second = LocalDownloadLease.Acquire(_root, "second.txt", payload))
            {
                using FileStream? beforeCreation = LocalDownloadLease.TryAcquireCleanup(_root);
                Assert.That(beforeCreation, Is.Null);
                File.WriteAllText(payload, "complete but not committed");
                using FileStream? afterClose = LocalDownloadLease.TryAcquireCleanup(_root);
                Assert.That(afterClose, Is.Null);
            }

            using FileStream? cleanup = LocalDownloadLease.TryAcquireCleanup(_root);
            Assert.That(cleanup, Is.Not.Null);
            File.Delete(payload);
            Assert.That(File.Exists(payload), Is.False);
        }

        [Test]
        public void CleanupLease_RejectsNewDownloadUntilReleased()
        {
            string target = Path.Combine(_root, "target.txt");
            using (FileStream? cleanup = LocalDownloadLease.TryAcquireCleanup(_root))
            {
                Assert.That(cleanup, Is.Not.Null);
                Assert.Throws<LocalFileUnavailableException>(() => LocalDownloadLease.Acquire(_root, "target.txt", target));
                Assert.That(File.Exists(target), Is.False);
            }

            using FileStream download = LocalDownloadLease.Acquire(_root, "target.txt", target);
        }

        [Test]
        public async Task ConcurrentDownloads_PreserveActiveTemporaryContent()
        {
            TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AtomicLocalFileSyncWriter firstWriter = new();
            AtomicLocalFileSyncWriter secondWriter = new();
            Task<LocalFileWriteResult> first = firstWriter.WriteFileAsync(_root, "first.txt", async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("first content"), token);
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(token);
            });
            try
            {
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await secondWriter.WriteFileAsync(_root, "second.txt", async (stream, token) =>
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("second content"), token));
            }
            finally
            {
                releaseFirst.TrySetResult();
                await first.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, "first.txt")), Is.EqualTo("first content"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "second.txt")), Is.EqualTo("second content"));
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });
        }

        [Test]
        public async Task FailedDownload_ReleasesReservationForCleanupAndRetry()
        {
            AtomicLocalFileSyncWriter writer = new();
            Assert.ThrowsAsync<IOException>(() => writer.WriteFileAsync(_root, "failed.txt", (_, _) =>
                throw new IOException("Injected transfer failure.")));
            string metadata = Path.Combine(_root, ".cotton-sync");
            using (FileStream? cleanup = LocalDownloadLease.TryAcquireCleanup(metadata))
            {
                Assert.That(cleanup, Is.Not.Null);
            }

            await writer.WriteFileAsync(_root, "failed.txt", async (stream, token) =>
                await stream.WriteAsync(Encoding.UTF8.GetBytes("recovered"), token));
            Assert.That(File.ReadAllText(Path.Combine(_root, "failed.txt")), Is.EqualTo("recovered"));
        }
    }
}
