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
        private async Task<VirtualPlaceholderPopulationSmokeResult> VerifyVirtualPlaceholderPopulationScaleAsync(
            string syncPairId,
            int fileCount,
            Func<int, string> relativePathFactory,
            TimeSpan smokeTarget,
            long managedHeapDeltaTargetBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(relativePathFactory);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileCount);

            List<RemoteFileSnapshot> remoteFiles = new(fileCount);
            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = relativePathFactory(index);
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = LightweightRemoteFile(relativePath, index),
                });
            }

            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler(remoteFiles);
            GuardedRemoteFileSynchronizer remoteFilesClient = new GuardedRemoteFileSynchronizer();
            CountingVirtualPlaceholderStateStore stateStore = new CountingVirtualPlaceholderStateStore();
            CountingRemoteFilePlaceholderWriter placeholderWriter = new CountingRemoteFilePlaceholderWriter();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            List<int> cooperativeYieldCompletedCounts = new List<int>();
            SyncEngine engine = new SyncEngine(
                new EmptyLocalFileScanner(),
                remoteCrawler,
                remoteFilesClient,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            MemorySample beforeRunMemory = CaptureMemorySample();
            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = syncPairId,
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                },
                new SyncRunOptions
                {
                    MaximumStoredResultActivities = 100,
                    RunProgress = runProgress,
                    CooperativeYieldAsync = _ =>
                    {
                        cooperativeYieldCompletedCounts.Add(placeholderWriter.Count);
                        return ValueTask.CompletedTask;
                    },
                });
            stopwatch.Stop();
            MemorySample afterRunMemory = CaptureMemorySample();

            List<SyncRunProgress> placeholderProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            long managedHeapDeltaBytes = afterRunMemory.ManagedHeapBytes - beforeRunMemory.ManagedHeapBytes;
            TestContext.WriteLine(
                "Virtual-files placeholder population for {0:N0} files completed in {1:N0} ms; placeholder writes {2:N0}; state upserts {3:N0}; retained activities {4:N0}/{5:N0}; progress samples {6:N0}; cooperative yields {7:N0}; managed heap delta {8:N1} MiB; working set delta {9:N1} MiB.",
                fileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                placeholderWriter.Count,
                stateStore.FileUpserts,
                result.Activities.Count,
                result.TotalActivityCount,
                placeholderProgress.Count,
                cooperativeYieldCompletedCounts.Count,
                ToMiB(managedHeapDeltaBytes),
                ToMiB(afterRunMemory.WorkingSetBytes - beforeRunMemory.WorkingSetBytes));

            Assert.Multiple(() =>
            {
                Assert.That(remoteFilesClient.UploadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DownloadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DeleteCalls, Is.Zero);
                Assert.That(remoteFilesClient.MoveCalls, Is.Zero);
                Assert.That(remoteCrawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.BeginPopulationCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.EndPopulationCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Count, Is.EqualTo(fileCount));
                Assert.That(stateStore.FileUpserts, Is.EqualTo(fileCount));
                Assert.That(stateStore.RemoteOnlyPlaceholderUpserts, Is.EqualTo(fileCount));
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(result.IsActivityListTruncated, Is.False);
                Assert.That(placeholderProgress, Is.Not.Empty);
                Assert.That(placeholderProgress.Any(progress => progress.FilesCompleted > 0 && progress.FilesCompleted < fileCount), Is.True);
                Assert.That(placeholderProgress.Last().FilesTotal, Is.EqualTo(fileCount));
                Assert.That(cooperativeYieldCompletedCounts, Is.Not.Empty);
                Assert.That(cooperativeYieldCompletedCounts, Has.All.GreaterThan(0));
                Assert.That(cooperativeYieldCompletedCounts, Has.All.LessThan(fileCount));
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
                Assert.That(managedHeapDeltaBytes, Is.LessThan(managedHeapDeltaTargetBytes));
            });

            return new VirtualPlaceholderPopulationSmokeResult(
                stopwatch.Elapsed,
                managedHeapDeltaBytes,
                placeholderWriter.Count,
                placeholderWriter.FirstRelativePath,
                placeholderWriter.LastRelativePath,
                placeholderProgress.Count,
                cooperativeYieldCompletedCounts.Count,
                result.Activities.Count,
                result.IsActivityListTruncated);
        }

        private async Task<VirtualPlaceholderRepeatPassSmokeResult> VerifyVirtualPlaceholderRepeatPassAvoidsFullLocalScanAsync(
            string syncPairId,
            int fileCount,
            Func<int, string> relativePathFactory,
            TimeSpan smokeTarget,
            Func<SyncStateEntry, int, SyncStateEntry>? stateEntryCustomizer = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(relativePathFactory);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileCount);

            List<RemoteFileSnapshot> remoteFiles = new(fileCount);
            List<SyncStateEntry> baselineEntries = new(fileCount);
            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = relativePathFactory(index);
                NodeFileManifestDto remoteFile = LightweightRemoteFile(relativePath, index);
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = remoteFile,
                });
                SyncStateEntry baselineEntry = CreateRemoteOnlyPlaceholderState(syncPairId, relativePath, remoteFile);
                baselineEntries.Add(stateEntryCustomizer?.Invoke(baselineEntry, index) ?? baselineEntry);
            }

            FailOnFullScanLocalFileScanner localScanner = new(baselineEntries);
            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler(remoteFiles);
            GuardedRemoteFileSynchronizer remoteFilesClient = new GuardedRemoteFileSynchronizer();
            CountingVirtualPlaceholderStateStore stateStore = new CountingVirtualPlaceholderStateStore(baselineEntries);
            CountingRemoteFilePlaceholderWriter placeholderWriter = new CountingRemoteFilePlaceholderWriter();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
                localScanner,
                remoteCrawler,
                remoteFilesClient,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = syncPairId,
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                },
                new SyncRunOptions
                {
                    MaximumStoredResultActivities = 100,
                    RunProgress = runProgress,
                });
            stopwatch.Stop();

            List<SyncRunProgress> placeholderProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            TestContext.WriteLine(
                "Virtual-files repeat pass for {0:N0} persisted placeholders completed in {1:N0} ms; state entries loaded {2:N0}; local full scans {3}; local path lookups {4}; streaming crawls {5}; placeholder writes {6}; retained activities {7:N0}/{8:N0}; progress samples {9:N0}.",
                fileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                stateStore.LoadPairEntriesYieldCount,
                localScanner.ScanCalls,
                localScanner.PathLookupCalls,
                remoteCrawler.StreamingCrawlCalls,
                placeholderWriter.Count,
                result.Activities.Count,
                result.TotalActivityCount,
                placeholderProgress.Count);

            Assert.Multiple(() =>
            {
                Assert.That(remoteFilesClient.UploadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DownloadCalls, Is.Zero);
                Assert.That(remoteFilesClient.DeleteCalls, Is.Zero);
                Assert.That(remoteFilesClient.MoveCalls, Is.Zero);
                Assert.That(remoteCrawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(stateStore.LoadPairEntriesCalls, Is.EqualTo(1));
                Assert.That(stateStore.LoadPairEntriesYieldCount, Is.EqualTo(fileCount));
                Assert.That(localScanner.ScanCalls, Is.Zero);
                Assert.That(localScanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Count, Is.Zero);
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(result.IsActivityListTruncated, Is.False);
                Assert.That(placeholderProgress, Is.Empty);
                Assert.That(stopwatch.Elapsed, Is.LessThan(smokeTarget));
            });

            return new VirtualPlaceholderRepeatPassSmokeResult(
                stopwatch.Elapsed,
                localScanner.ScanCalls,
                placeholderWriter.Count,
                stateStore.LoadPairEntriesYieldCount,
                remoteCrawler.StreamingCrawlCalls,
                result.Activities.Count,
                result.TotalActivityCount,
                placeholderProgress.Count);
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        private static TimeSpan CalculateStageElapsed(
            IReadOnlyList<SyncRunProgress> progress,
            SyncRunProgressStage startStage,
            SyncRunProgressStage nextStage)
        {
            SyncRunProgress start = progress.First(item => item.Stage == startStage);
            SyncRunProgress next = progress.First(item => item.Stage == nextStage);
            TimeSpan elapsed = next.OccurredAtUtc - start.OccurredAtUtc;
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        private static byte[] CreateDeterministicBytes(int length)
        {
            byte[] bytes = new byte[length];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(((index * 31) + (index / 17)) % 251);
            }

            return bytes;
        }

        private static MemorySample CaptureMemorySample()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new MemorySample(GC.GetTotalMemory(forceFullCollection: false), process.WorkingSet64);
        }

        private static double ToMiB(long bytes)
        {
            return bytes / (double)MiB;
        }

        private static NodeFileManifestDto RemoteFile(string relativePath, string contentHash, long sizeBytes)
        {
            return new NodeFileManifestDto
            {
                Id = Guid.NewGuid(),
                CreatedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc),
                NodeId = RemoteRootNodeId,
                FileManifestId = Guid.NewGuid(),
                OriginalNodeFileId = Guid.NewGuid(),
                OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = relativePath.Split('/')[^1],
                ContentType = "text/plain",
                SizeBytes = sizeBytes,
                ContentHash = contentHash,
                ETag = "sha256-" + contentHash,
                Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
            };
        }

        private static NodeFileManifestDto LightweightRemoteFile(string relativePath, int index)
        {
            string hash = index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            return new NodeFileManifestDto
            {
                Id = GuidFromIndex(index, 1),
                CreatedAt = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
                NodeId = GuidFromIndex(index, 2),
                FileManifestId = GuidFromIndex(index, 3),
                OriginalNodeFileId = GuidFromIndex(index, 4),
                OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = Path.GetFileName(relativePath),
                ContentType = "application/octet-stream",
                SizeBytes = 128 + index,
                ContentHash = hash,
                ETag = "sha256-" + hash,
                Metadata = [],
            };
        }

        private static SyncStateEntry CreateRemoteOnlyPlaceholderState(
            string syncPairId,
            string relativePath,
            NodeFileManifestDto remoteFile)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPairId,
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = remoteFile.Id,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
            };
        }

        private static Guid GuidFromIndex(int index, byte salt)
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes, index);
            bytes[15] = salt;
            return new Guid(bytes);
        }

    }
}
