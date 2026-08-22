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
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesTenThousandPlaceholdersWithinSmokeTarget()
        {
            VirtualPlaceholderPopulationSmokeResult smoke = await VerifyVirtualPlaceholderPopulationScaleAsync(
                "performance-vfs-placeholders-10k",
                fileCount: 10_000,
                relativePathFactory: index => "LargeTree/file-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
                smokeTarget: TimeSpan.FromSeconds(20),
                managedHeapDeltaTargetBytes: 160L * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(smoke.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
                Assert.That(smoke.CooperativeYieldCount, Is.GreaterThanOrEqualTo(300));
                Assert.That(smoke.RunProgressCount, Is.GreaterThanOrEqualTo(300));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesSixtyThousandNodeModulesPlaceholdersResponsively()
        {
            VirtualPlaceholderPopulationSmokeResult smoke = await VerifyVirtualPlaceholderPopulationScaleAsync(
                "performance-vfs-placeholders-node-modules-60k",
                fileCount: 60_000,
                relativePathFactory: index =>
                    "node_modules/package-"
                    + (index / 100).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                    + "/dist/file-"
                    + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                    + ".js",
                smokeTarget: TimeSpan.FromSeconds(60),
                managedHeapDeltaTargetBytes: 256L * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(smoke.Elapsed, Is.LessThan(TimeSpan.FromSeconds(60)));
                Assert.That(smoke.CooperativeYieldCount, Is.GreaterThanOrEqualTo(500));
                Assert.That(smoke.RunProgressCount, Is.GreaterThanOrEqualTo(500));
                Assert.That(smoke.FirstPlaceholderPath, Does.StartWith("node_modules/"));
                Assert.That(smoke.LastPlaceholderPath, Does.StartWith("node_modules/"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesOneHundredThousandPlaceholdersWithBoundedMemory()
        {
            VirtualPlaceholderPopulationSmokeResult smoke = await VerifyVirtualPlaceholderPopulationScaleAsync(
                "performance-vfs-placeholders-100k",
                fileCount: 100_000,
                relativePathFactory: index => "HugeTree/"
                    + (index / 1_000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
                    + "/file-"
                    + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                    + ".bin",
                smokeTarget: TimeSpan.FromSeconds(90),
                managedHeapDeltaTargetBytes: 384L * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(smoke.Elapsed, Is.LessThan(TimeSpan.FromSeconds(90)));
                Assert.That(smoke.ManagedHeapDeltaBytes, Is.LessThan(384L * MiB));
                Assert.That(smoke.CooperativeYieldCount, Is.GreaterThanOrEqualTo(900));
                Assert.That(smoke.RetainedActivityCount, Is.Zero);
                Assert.That(smoke.IsActivityListTruncated, Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRepeatPassOnOneHundredThousandPlaceholdersAvoidsFullLocalScan()
        {
            VirtualPlaceholderRepeatPassSmokeResult smoke =
                await VerifyVirtualPlaceholderRepeatPassAvoidsFullLocalScanAsync(
                    "performance-vfs-repeat-100k",
                    fileCount: 100_000,
                    relativePathFactory: index => "HugeTree/"
                        + (index / 1_000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
                        + "/file-"
                        + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                        + ".bin",
                    smokeTarget: TimeSpan.FromSeconds(15));

            Assert.Multiple(() =>
            {
                Assert.That(smoke.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
                Assert.That(smoke.LocalFullScanCalls, Is.Zero);
                Assert.That(smoke.PlaceholderWrites, Is.Zero);
                Assert.That(smoke.StateEntriesLoaded, Is.EqualTo(100_000));
                Assert.That(smoke.StreamingCrawlCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRepeatPassAndHydratedStateAvoidsFullLocalScan()
        {
            VirtualPlaceholderRepeatPassSmokeResult smoke =
                await VerifyVirtualPlaceholderRepeatPassAvoidsFullLocalScanAsync(
                    "performance-vfs-repeat-hydrated-100k",
                    fileCount: 100_000,
                    relativePathFactory: index => "HugeTree/"
                        + (index / 1_000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
                        + "/file-"
                        + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                        + ".bin",
                    smokeTarget: TimeSpan.FromSeconds(15),
                    stateEntryCustomizer: (entry, index) =>
                    {
                        if (index == 50_000)
                        {
                            entry.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                            entry.LocalContentHash = entry.RemoteContentHash;
                            entry.LocalSizeBytes = entry.RemoteSizeBytes;
                            entry.LocalLastWriteUtc = entry.SyncedAtUtc;
                        }

                        return entry;
                    });

            Assert.Multiple(() =>
            {
                Assert.That(smoke.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
                Assert.That(smoke.LocalFullScanCalls, Is.Zero);
                Assert.That(smoke.PlaceholderWrites, Is.Zero);
                Assert.That(smoke.StateEntriesLoaded, Is.EqualTo(100_000));
                Assert.That(smoke.StreamingCrawlCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesPlaceholdersConcurrentlyWithinConfiguredLimit()
        {
            const int fileCount = 64;
            const int placeholderConcurrency = 4;
            List<RemoteFileSnapshot> remoteFiles = new(fileCount);
            for (int index = 0; index < fileCount; index++)
            {
                string relativePath = "Concurrent/file-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".txt";
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = LightweightRemoteFile(relativePath, index),
                });
            }

            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler(remoteFiles);
            CountingVirtualPlaceholderStateStore stateStore = new CountingVirtualPlaceholderStateStore();
            CountingRemoteFilePlaceholderWriter placeholderWriter = new CountingRemoteFilePlaceholderWriter
            {
                OperationDelay = TimeSpan.FromMilliseconds(25),
            };
            SyncEngine engine = new SyncEngine(
                new EmptyLocalFileScanner(),
                remoteCrawler,
                new GuardedRemoteFileSynchronizer(),
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = "performance-vfs-placeholder-concurrency",
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                },
                new SyncRunOptions
                {
                    InitialVirtualFilesPlaceholderConcurrency = placeholderConcurrency,
                });

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Count, Is.EqualTo(fileCount));
                Assert.That(stateStore.FileUpserts, Is.EqualTo(fileCount));
                Assert.That(placeholderWriter.MaxConcurrent, Is.GreaterThan(1));
                Assert.That(placeholderWriter.MaxConcurrent, Is.LessThanOrEqualTo(placeholderConcurrency));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesBatchesInitialDirectoryStateWrites()
        {
            const int directoryCount = 1_024;
            const int stateBatchSize = 128;
            List<RemoteDirectorySnapshot> remoteDirectories = Enumerable
                .Range(0, directoryCount)
                .Select(index =>
                {
                    string relativePath = "dir-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    return new RemoteDirectorySnapshot
                    {
                        RelativePath = relativePath,
                        Node = new NodeDto
                        {
                            Id = GuidFromIndex(index, 9),
                            Name = relativePath,
                        },
                    };
                })
                .ToList();
            StaticRemoteTreeCrawler remoteCrawler = new StaticRemoteTreeCrawler([], remoteDirectories);
            CountingVirtualPlaceholderStateStore stateStore = new CountingVirtualPlaceholderStateStore();
            CountingRemoteFilePlaceholderWriter placeholderWriter = new CountingRemoteFilePlaceholderWriter();
            SyncEngine engine = new SyncEngine(
                new EmptyLocalFileScanner(),
                remoteCrawler,
                new GuardedRemoteFileSynchronizer(),
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                new SyncPair
                {
                    SyncPairId = "performance-vfs-directory-batch",
                    LocalRootPath = _root,
                    RemoteRootNodeId = RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                },
                new SyncRunOptions
                {
                    InitialVirtualFilesStateBatchSize = stateBatchSize,
                });

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.FullCrawlCalls, Is.Zero);
                Assert.That(stateStore.SingleUpsertCalls, Is.Zero);
                Assert.That(stateStore.DirectoryUpserts, Is.EqualTo(directoryCount));
                Assert.That(stateStore.UpsertManyCalls, Is.EqualTo(directoryCount / stateBatchSize));
                Assert.That(stateStore.UpsertManyEntryCounts, Is.All.EqualTo(stateBatchSize));
            });
        }

    }
}
