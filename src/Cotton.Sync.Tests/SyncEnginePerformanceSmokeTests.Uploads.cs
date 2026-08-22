// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests
{
    public partial class SyncEnginePerformanceSmokeTests
    {
        [Test]
        [Explicit("Timing-sensitive upload smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_UploadsOneThousandSmallFilesWithinSmokeTarget()
        {
            await VerifyInitialUploadFileSetCompletesWithinSmokeTargetAsync(
                "performance-upload-small",
                fileCount: 1_000,
                smokeTarget: TimeSpan.FromSeconds(30),
                managedHeapDeltaTargetBytes: 160L * MiB);
        }

        [Test]
        [Explicit("Timing-sensitive upload smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_UploadsThreeThousandSmallFilesWithinSmokeTarget()
        {
            await VerifyInitialUploadFileSetCompletesWithinSmokeTargetAsync(
                "performance-upload-small-3k",
                fileCount: 3_000,
                smokeTarget: TimeSpan.FromSeconds(90),
                managedHeapDeltaTargetBytes: 256L * MiB);
        }

        [Test]
        [Explicit("Release-scale smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_UploadsTenThousandSmallFilesWithinManualSmokeTarget()
        {
            await VerifyInitialUploadFileSetCompletesWithinSmokeTargetAsync(
                "performance-upload-small-10k",
                fileCount: 10_000,
                smokeTarget: TimeSpan.FromMinutes(5),
                managedHeapDeltaTargetBytes: 512L * MiB);
        }

        [Test]
        [Explicit("Release-scale smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_UploadsThirtyThousandSmallFilesWithinManualSmokeTarget()
        {
            await VerifyInitialUploadFileSetCompletesWithinSmokeTargetAsync(
                "performance-upload-small-30k",
                fileCount: 30_000,
                smokeTarget: TimeSpan.FromMinutes(12),
                managedHeapDeltaTargetBytes: 1_024L * MiB);
        }

        [Test]
        public async Task RunOnceAsync_UploadsOneLargeFileWithinSmokeTarget()
        {
            const int fileSizeBytes = 8 * 1024 * 1024;
            const string relativePath = "Large/single-large.bin";
            TimeSpan smokeTarget = TimeSpan.FromSeconds(15);
            byte[] content = CreateDeterministicBytes(fileSizeBytes);
            string expectedHash = Hash(content);
            WriteFile(relativePath, content);
            SqliteSyncStateStore stateStore = new(_databasePath);
            RecordingRemoteFileSynchronizer remoteFilesClient = new RecordingRemoteFileSynchronizer();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
                new LocalFileScanner(),
                new StaticRemoteTreeCrawler([]),
                remoteFilesClient,
                stateStore);

            Stopwatch stopwatch = Stopwatch.StartNew();
            remoteFilesClient.MeasurementStopwatch = stopwatch;
            SyncRunResult result = await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = "performance-upload-large",
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                },
                new SyncRunOptions { RunProgress = runProgress });
            stopwatch.Stop();

            SyncStateEntry? baseline = await stateStore.GetAsync("performance-upload-large", relativePath);
            TimeSpan localScanElapsed = CalculateStageElapsed(
                runProgress.Values,
                SyncRunProgressStage.ScanningLocal,
                SyncRunProgressStage.ScanningRemote);
            TestContext.WriteLine(
                "Initial upload smoke for one {0:N0}-byte file completed in {1:N0} ms; local metadata scan {2:N0} ms; first upload started after {3:N0} ms.",
                fileSizeBytes,
                stopwatch.Elapsed.TotalMilliseconds,
                localScanElapsed.TotalMilliseconds,
                remoteFilesClient.UploadStartedAt.Single().TotalMilliseconds);

            Assert.Multiple(() =>
            {
                Assert.That(remoteFilesClient.UploadCalls, Is.EqualTo(1));
                Assert.That(remoteFilesClient.DownloadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DeleteCalls, Is.Zero);
                Assert.That(remoteFilesClient.Uploads.Single().RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFilesClient.UploadInputContentHashes.Single(), Is.EqualTo(expectedHash));
                Assert.That(remoteFilesClient.Uploads.Single().LocalFile.SizeBytes, Is.EqualTo(fileSizeBytes));
                Assert.That(remoteFilesClient.Uploads.Single().LocalFile.ContentHash, Is.EqualTo(expectedHash));
                Assert.That(localScanElapsed, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(baseline, Is.Not.Null);
                Assert.That(baseline!.LocalContentHash, Is.EqualTo(expectedHash));
                Assert.That(baseline.RemoteContentHash, Is.EqualTo(expectedHash));
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
            });
        }

        private string FullPath(string relativePath)
        {
            return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private void WriteFile(string relativePath, byte[] content)
        {
            string fullPath = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
            File.SetLastWriteTimeUtc(fullPath, new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc));
        }

    }
}
