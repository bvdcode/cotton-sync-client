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
        private async Task VerifyNoOpFileSetCompletesWithinSmokeTargetAsync(
            string syncPairId,
            int fileCount,
            TimeSpan smokeTarget,
            long managedHeapDeltaTargetBytes)
        {
            SqliteSyncStateStore stateStore = new(_databasePath);
            await stateStore.InitializeAsync();
            List<RemoteFileSnapshot> remoteFiles = [];
            List<SyncStateEntry> baselineEntries = [];

            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = $"Docs/{index / 100:D2}/file-{index:D5}.txt";
                byte[] content = Encoding.UTF8.GetBytes("content-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                string hash = Hash(content);
                WriteFile(relativePath, content);
                NodeFileManifestDto remoteFile = RemoteFile(relativePath, hash, content.Length);
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = remoteFile,
                });
                baselineEntries.Add(new SyncStateEntry
                {
                    SyncPairId = syncPairId,
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.File,
                    LocalContentHash = hash,
                    LocalLastWriteUtc = File.GetLastWriteTimeUtc(FullPath(relativePath)),
                    LocalSizeBytes = content.Length,
                    RemoteNodeId = remoteFile.NodeId,
                    RemoteFileId = remoteFile.Id,
                    RemoteContentHash = remoteFile.ContentHash,
                    RemoteETag = remoteFile.ETag,
                    SyncedAtUtc = DateTime.UtcNow,
                });
            }

            await stateStore.ReplacePairAsync(syncPairId, baselineEntries);

            GuardedRemoteFileSynchronizer remoteFilesClient = new GuardedRemoteFileSynchronizer();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
                new LocalFileScanner(),
                new StaticRemoteTreeCrawler(remoteFiles),
                remoteFilesClient,
                stateStore);

            MemorySample beforeRunMemory = CaptureMemorySample();
            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = await engine.RunOnceAsync(new SyncPair
            {
                SyncPairId = syncPairId,
                LocalRootPath = _root,
                RemoteRootNodeId = RemoteRootNodeId,
            }, new SyncRunOptions { RunProgress = runProgress });
            stopwatch.Stop();
            MemorySample afterRunMemory = CaptureMemorySample();

            IReadOnlyList<SyncStateEntry> baselines = await stateStore.LoadPairAsync(syncPairId);
            TimeSpan localScanElapsed = CalculateStageElapsed(
                runProgress.Values,
                SyncRunProgressStage.ScanningLocal,
                SyncRunProgressStage.ScanningRemote);
            TimeSpan remoteScanElapsed = CalculateStageElapsed(
                runProgress.Values,
                SyncRunProgressStage.ScanningRemote,
                SyncRunProgressStage.ReconcilingDirectories);
            TimeSpan directoryReconcileElapsed = CalculateStageElapsed(
                runProgress.Values,
                SyncRunProgressStage.ReconcilingDirectories,
                SyncRunProgressStage.ReconcilingFiles);
            TimeSpan fileReconcileElapsed = CalculateStageElapsed(
                runProgress.Values,
                SyncRunProgressStage.ReconcilingFiles,
                SyncRunProgressStage.Completed);
            TestContext.WriteLine(
                "No-op sync smoke for {0} files completed in {1:N0} ms; local scan {2:N0} ms; remote scan {3:N0} ms; directory reconcile {4:N0} ms; file reconcile {5:N0} ms; managed heap delta {6:N1} MiB; working set delta {7:N1} MiB.",
                fileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                localScanElapsed.TotalMilliseconds,
                remoteScanElapsed.TotalMilliseconds,
                directoryReconcileElapsed.TotalMilliseconds,
                fileReconcileElapsed.TotalMilliseconds,
                ToMiB(afterRunMemory.ManagedHeapBytes - beforeRunMemory.ManagedHeapBytes),
                ToMiB(afterRunMemory.WorkingSetBytes - beforeRunMemory.WorkingSetBytes));

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(remoteFilesClient.UploadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DownloadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DeleteCalls, Is.Zero);
                Assert.That(baselines, Has.Count.EqualTo(fileCount));
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
                Assert.That(
                    afterRunMemory.ManagedHeapBytes - beforeRunMemory.ManagedHeapBytes,
                    Is.LessThan(managedHeapDeltaTargetBytes));
            });
        }

        private async Task VerifyScopedLocalChangeAvoidsFullTreeScanAsync(
            string syncPairId,
            int fileCount,
            TimeSpan smokeTarget)
        {
            string changedPath = $"Docs/{(fileCount - 1) / 100:D2}/file-{fileCount - 1:D5}.txt";
            SqliteSyncStateStore stateStore = new(_databasePath);
            await stateStore.InitializeAsync();
            List<RemoteFileSnapshot> remoteFiles = [];
            List<SyncStateEntry> baselineEntries = [];

            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = $"Docs/{index / 100:D2}/file-{index:D5}.txt";
                byte[] content = Encoding.UTF8.GetBytes("content-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                string hash = Hash(content);
                WriteFile(relativePath, content);
                NodeFileManifestDto remoteFile = RemoteFile(relativePath, hash, content.Length);
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = remoteFile,
                });
                baselineEntries.Add(new SyncStateEntry
                {
                    SyncPairId = syncPairId,
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.File,
                    LocalContentHash = hash,
                    LocalLastWriteUtc = File.GetLastWriteTimeUtc(FullPath(relativePath)),
                    LocalSizeBytes = content.Length,
                    RemoteNodeId = remoteFile.NodeId,
                    RemoteFileId = remoteFile.Id,
                    RemoteContentHash = remoteFile.ContentHash,
                    RemoteETag = remoteFile.ETag,
                    SyncedAtUtc = DateTime.UtcNow,
                });
            }

            await stateStore.ReplacePairAsync(syncPairId, baselineEntries);
            WriteFile(changedPath, Encoding.UTF8.GetBytes("changed-content"));

            RecordingRemoteFileSynchronizer remoteFilesClient = new RecordingRemoteFileSynchronizer();
            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler(remoteFiles);
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
                new LocalFileScanner(),
                remoteCrawler,
                remoteFilesClient,
                stateStore);

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
                    RunProgress = runProgress,
                });
            stopwatch.Stop();

            TestContext.WriteLine(
                "Scoped local change in {0} file tree completed in {1:N0} ms; path crawls {2}; full crawls {3}; uploads {4}; first upload started after {5:N0} ms.",
                fileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                remoteCrawler.PathCrawlCalls,
                remoteCrawler.FullCrawlCalls,
                remoteFilesClient.UploadCalls,
                remoteFilesClient.UploadStartedAt.Single().TotalMilliseconds);

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFilesClient.UploadCalls, Is.EqualTo(1));
                Assert.That(remoteFilesClient.Uploads.Single().RelativePath, Is.EqualTo(changedPath));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { changedPath }));
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
            });
        }

        private async Task VerifyInitialUploadFileSetCompletesWithinSmokeTargetAsync(
            string syncPairId,
            int fileCount,
            TimeSpan smokeTarget,
            long managedHeapDeltaTargetBytes)
        {
            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = $"Upload/{index / 100:D2}/small-{index:D5}.txt";
                byte[] content = Encoding.UTF8.GetBytes("small-upload-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                WriteFile(relativePath, content);
            }

            SqliteSyncStateStore stateStore = new(_databasePath);
            RecordingRemoteFileSynchronizer remoteFilesClient = new RecordingRemoteFileSynchronizer();
            RecordingProgress<SyncActivity> activityProgress = new RecordingProgress<SyncActivity>();
            const int retainedActivityLimit = 100;
            SyncEngine engine = new SyncEngine(
                new LocalFileScanner(),
                new StaticRemoteTreeCrawler([]),
                remoteFilesClient,
                stateStore);

            MemorySample beforeRunMemory = CaptureMemorySample();
            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = syncPairId,
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                },
                new SyncRunOptions
                {
                    ActivityProgress = activityProgress,
                    MaximumStoredResultActivities = retainedActivityLimit,
                });
            stopwatch.Stop();
            MemorySample afterRunMemory = CaptureMemorySample();

            IReadOnlyList<SyncStateEntry> baselines = await stateStore.LoadPairAsync(syncPairId);
            int distinctRemoteFileIds = baselines
                .Select(entry => entry.RemoteFileId)
                .Where(id => id.HasValue)
                .Distinct()
                .Count();
            TestContext.WriteLine(
                "Initial upload smoke for {0} small files completed in {1:N0} ms; managed heap delta {2:N1} MiB; working set delta {3:N1} MiB.",
                fileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                ToMiB(afterRunMemory.ManagedHeapBytes - beforeRunMemory.ManagedHeapBytes),
                ToMiB(afterRunMemory.WorkingSetBytes - beforeRunMemory.WorkingSetBytes));

            Assert.Multiple(() =>
            {
                Assert.That(remoteFilesClient.UploadCalls, Is.EqualTo(fileCount));
                Assert.That(remoteFilesClient.DownloadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DeleteCalls, Is.Zero);
                Assert.That(activityProgress.Values, Has.Count.EqualTo(fileCount));
                Assert.That(result.TotalActivityCount, Is.EqualTo(fileCount));
                Assert.That(result.Activities, Has.Count.EqualTo(Math.Min(fileCount, retainedActivityLimit)));
                Assert.That(result.IsActivityListTruncated, Is.EqualTo(fileCount > retainedActivityLimit));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.All.EqualTo(SyncActivityKind.Uploaded));
                Assert.That(baselines, Has.Count.EqualTo(fileCount));
                Assert.That(distinctRemoteFileIds, Is.EqualTo(fileCount));
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
                Assert.That(
                    afterRunMemory.ManagedHeapBytes - beforeRunMemory.ManagedHeapBytes,
                    Is.LessThan(managedHeapDeltaTargetBytes));
            });
        }

    }
}
