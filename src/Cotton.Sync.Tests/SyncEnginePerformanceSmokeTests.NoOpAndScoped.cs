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
        public async Task RunOnceAsync_NoOpForOneThousandFilesCompletesWithinSmokeTarget()
        {
            const int fileCount = 1_000;
            TimeSpan smokeTarget = TimeSpan.FromSeconds(20);

            await VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
                "performance-noop-1k",
                fileCount,
                smokeTarget,
                managedHeapDeltaTargetBytes: 128L * MiB);
        }

        [Test]
        public async Task RunOnceAsync_NoOpForThreeThousandFilesCompletesWithinSmokeTarget()
        {
            const int fileCount = 3_000;
            TimeSpan smokeTarget = TimeSpan.FromSeconds(60);

            await VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
                "performance-noop-3k",
                fileCount,
                smokeTarget,
                managedHeapDeltaTargetBytes: 160L * MiB);
        }

        [Test]
        [Explicit("Release-scale smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_NoOpForTenThousandFilesCompletesWithinManualSmokeTarget()
        {
            const int fileCount = 10_000;
            TimeSpan smokeTarget = TimeSpan.FromMinutes(3);

            await VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
                "performance-noop-10k",
                fileCount,
                smokeTarget,
                managedHeapDeltaTargetBytes: 256L * MiB);
        }

        [Test]
        [Explicit("Release-scale smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_NoOpForThirtyThousandFilesCompletesWithinManualSmokeTarget()
        {
            const int fileCount = 30_000;
            TimeSpan smokeTarget = TimeSpan.FromMinutes(8);

            await VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
                "performance-noop-30k",
                fileCount,
                smokeTarget,
                managedHeapDeltaTargetBytes: 512L * MiB);
        }

        [Test]
        [Explicit("Release-scale smoke; run manually before release or on dedicated Windows performance verification.")]
        public async Task RunOnceAsync_NoOpForFiftyThousandFilesCompletesWithinManualSmokeTarget()
        {
            const int fileCount = 50_000;
            TimeSpan smokeTarget = TimeSpan.FromMinutes(12);

            await VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
                "performance-noop-50k",
                fileCount,
                smokeTarget,
                managedHeapDeltaTargetBytes: 768L * MiB);
        }

        [Test]
        [Explicit("Release-scale hot-path smoke; run manually before release on Windows.")]
        public async Task RunOnceAsync_ScopedLocalChangeInFiftyThousandFileTreeAvoidsFullTreeScan()
        {
            await VerifyScopedLocalChangeAvoidsFullTreeScanAsync(
                "performance-scoped-change-50k",
                fileCount: 50_000,
                smokeTarget: TimeSpan.FromSeconds(5));
        }

        [Test]
        [Explicit("Release-scale hot-path smoke; run manually before release on Windows.")]
        public async Task RunOnceAsync_ScopedLocalChangeInOneHundredThousandFileTreeAvoidsFullTreeScan()
        {
            await VerifyScopedLocalChangeAvoidsFullTreeScanAsync(
                "performance-scoped-change-100k",
                fileCount: 100_000,
                smokeTarget: TimeSpan.FromSeconds(10));
        }

        [Test]
        public async Task RunOnceAsync_ScopedLocalChangeInOneMillionLogicalEntryStateUsesOnlyScopedLookups()
        {
            const int logicalEntryCount = 1_000_000;
            const string syncPairId = "performance-scoped-change-1m-logical";
            const string changedPath = "Docs/9999/file-999999.txt";
            string oldHash = Hash(Encoding.UTF8.GetBytes("old-content"));
            string newHash = Hash(Encoding.UTF8.GetBytes("new-content"));
            ScopedPathOnlyLocalScanner localScanner = new ScopedPathOnlyLocalScanner(
                changedPath,
                new LocalFileSnapshot
                {
                    RelativePath = changedPath,
                    FullPath = FullPath(changedPath),
                    ContentHash = newHash,
                    SizeBytes = 11,
                    LastWriteUtc = new DateTime(2026, 6, 3, 13, 0, 0, DateTimeKind.Utc),
                });
            NodeFileManifestDto remoteFile = RemoteFile(changedPath, oldHash, sizeBytes: 11);
            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler(
            [
                new RemoteFileSnapshot
                {
                    RelativePath = changedPath,
                    File = remoteFile,
                },
            ]);
            RecordingRemoteFileSynchronizer remoteFilesClient = new RecordingRemoteFileSynchronizer();
            CountingScopedStateStore stateStore = new CountingScopedStateStore(
                logicalEntryCount,
                new SyncStateEntry
                {
                    SyncPairId = syncPairId,
                    RelativePath = changedPath,
                    Kind = SyncEntryKind.File,
                    LocalContentHash = oldHash,
                    LocalLastWriteUtc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc),
                    LocalSizeBytes = 11,
                    RemoteNodeId = remoteFile.NodeId,
                    RemoteFileId = remoteFile.Id,
                    RemoteContentHash = remoteFile.ContentHash,
                    RemoteETag = remoteFile.ETag,
                    SyncedAtUtc = new DateTime(2026, 6, 3, 12, 5, 0, DateTimeKind.Utc),
                });
            SyncEngine engine = new SyncEngine(localScanner, remoteCrawler, remoteFilesClient, stateStore);

            Stopwatch stopwatch = Stopwatch.StartNew();
            remoteFilesClient.MeasurementStopwatch = stopwatch;
            SyncRunResult result = await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = syncPairId,
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                },
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([changedPath]),
                });
            stopwatch.Stop();

            TestContext.WriteLine(
                "Scoped local change with {0:N0} logical state entries completed in {1:N0} ms; state GetAsync calls {2}; full state loads {3}; path crawls {4}; full crawls {5}; uploads {6}; first upload started after {7:N0} ms.",
                stateStore.LogicalEntryCount,
                stopwatch.Elapsed.TotalMilliseconds,
                stateStore.GetCalls,
                stateStore.FullLoadCalls,
                remoteCrawler.PathCrawlCalls,
                remoteCrawler.FullCrawlCalls,
                remoteFilesClient.UploadCalls,
                remoteFilesClient.UploadStartedAt.Single().TotalMilliseconds);

            Assert.Multiple(() =>
            {
                Assert.That(localScanner.FullScanCalls, Is.Zero);
                Assert.That(localScanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.FullCrawlCalls, Is.Zero);
                Assert.That(stateStore.FullLoadCalls, Is.Zero);
                Assert.That(stateStore.GetCalls, Is.EqualTo(3));
                Assert.That(stateStore.UpsertCalls, Is.EqualTo(1));
                Assert.That(remoteFilesClient.UploadCalls, Is.EqualTo(1));
                Assert.That(remoteFilesClient.Uploads.Single().RelativePath, Is.EqualTo(changedPath));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { changedPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            });
        }

    }
}
