// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public class LocalOfflineChangeDetectorTests
    {
        [Test]
        public async Task DetectAsync_OfflineFileRenameReturnsSourceAndTargetScope()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddDirectory(scanner.Snapshot, "Docs");
            AddFile(scanner.Snapshot, "Docs/renamed.txt", 27, Utc(12));

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                await store.UpsertManyAsync(
                [
                    DirectoryState(syncPair, "Docs"),
                    FileState(syncPair, "Docs/source.txt", 21, Utc(10)),
                ]);
                var detector = new LocalOfflineChangeDetector(scanner, store);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.Multiple(() =>
                {
                    Assert.That(request, Is.Not.Null);
                    Assert.That(request!.IsFull, Is.False);
                    Assert.That(request.Causes, Is.EqualTo(SyncRunCause.LocalChange));
                    Assert.That(request.LocalChangedPaths, Is.EqualTo(new[]
                    {
                        "Docs/renamed.txt",
                        "Docs/source.txt",
                    }));
                    Assert.That(request.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/source.txt" }));
                });
            });
        }

        [Test]
        public async Task DetectAsync_MetadataEditReturnsOnlyEditedFile()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddDirectory(scanner.Snapshot, "Docs");
            AddFile(scanner.Snapshot, "Docs/edited.txt", 25, Utc(11));
            AddFile(scanner.Snapshot, "Docs/unchanged.txt", 20, Utc(10));

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                await store.UpsertManyAsync(
                [
                    DirectoryState(syncPair, "Docs"),
                    FileState(syncPair, "Docs/edited.txt", 20, Utc(10)),
                    FileState(syncPair, "Docs/unchanged.txt", 20, Utc(10)),
                ]);
                var detector = new LocalOfflineChangeDetector(scanner, store);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.Multiple(() =>
                {
                    Assert.That(request?.LocalChangedPaths, Is.EqualTo(new[] { "Docs/edited.txt" }));
                    Assert.That(request?.LocalDeletedPaths, Is.Empty);
                });
            });
        }

        [Test]
        public async Task DetectAsync_OnlineOnlyPlaceholderWithoutLocalBaselineIsUnchanged()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddDirectory(scanner.Snapshot, "Docs");
            AddFile(
                scanner.Snapshot,
                "Docs/cloud.txt",
                1024,
                Utc(10),
                isCloudFilesPlaceholder: true,
                isOnlineOnly: true);

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                await store.UpsertManyAsync(
                [
                    DirectoryState(syncPair, "Docs"),
                    new SyncStateEntry
                    {
                        SyncPairId = syncPair.Id.ToString("D"),
                        RelativePath = "Docs/cloud.txt",
                        Kind = SyncEntryKind.File,
                        RemoteSizeBytes = 1024,
                        PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                    },
                ]);
                var detector = new LocalOfflineChangeDetector(scanner, store);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.That(request, Is.Null);
            });
        }

        [Test]
        public async Task DetectAsync_UnchangedProviderCreatedUntrackedFileIsSkipped()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddFile(scanner.Snapshot, "Docs/report (Cotton conflict 20260804T060000Z).txt", 24, Utc(10));
            var marker = new FakeProviderFileMarker(isUnchanged: true);

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                var detector = new LocalOfflineChangeDetector(scanner, store, marker);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.Multiple(() =>
                {
                    Assert.That(request, Is.Null);
                    Assert.That(marker.InspectedPaths, Is.EqualTo(new[]
                    {
                        "Docs/report (Cotton conflict 20260804T060000Z).txt",
                    }));
                });
            });
        }

        [Test]
        public async Task DetectAsync_ChangedProviderCreatedUntrackedFileIsReturned()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddFile(scanner.Snapshot, "Docs/recovery.txt", 31, Utc(11));
            var marker = new FakeProviderFileMarker(isUnchanged: false);

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                var detector = new LocalOfflineChangeDetector(scanner, store, marker);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.That(request?.LocalChangedPaths, Is.EqualTo(new[] { "Docs/recovery.txt" }));
            });
        }

        [Test]
        public async Task DetectAsync_NewAndDeletedDirectoriesCollapseDescendants()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            AddDirectory(scanner.Snapshot, "New");
            AddDirectory(scanner.Snapshot, "New/Nested");
            AddFile(scanner.Snapshot, "New/Nested/file.txt", 10, Utc(10));

            await WithStateStoreAsync(async store =>
            {
                await AddCompletedCursorAsync(store, syncPair);
                await store.UpsertManyAsync(
                [
                    DirectoryState(syncPair, "Old"),
                    DirectoryState(syncPair, "Old/Nested"),
                    FileState(syncPair, "Old/Nested/file.txt", 10, Utc(10)),
                ]);
                var detector = new LocalOfflineChangeDetector(scanner, store);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.Multiple(() =>
                {
                    Assert.That(request?.LocalChangedPaths, Is.EqualTo(new[] { "New", "Old" }));
                    Assert.That(request?.LocalDeletedPaths, Is.EqualTo(new[] { "Old" }));
                });
            });
        }

        [Test]
        public async Task DetectAsync_IncompleteInitialPopulationSkipsLocalSnapshot()
        {
            SyncPairSettings syncPair = CreatePair();
            var scanner = new FakeMetadataScanner();
            await WithStateStoreAsync(async store =>
            {
                var detector = new LocalOfflineChangeDetector(scanner, store);

                SyncRunRequest? request = await detector.DetectAsync(syncPair);

                Assert.Multiple(() =>
                {
                    Assert.That(request, Is.Null);
                    Assert.That(scanner.ScanCallCount, Is.Zero);
                });
            });
        }

        private static SyncPairSettings CreatePair()
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Cloud",
                LocalRootPath = "C:\\Cloud",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }

        private static DateTime Utc(int minute)
        {
            return new DateTime(2026, 8, 3, 12, minute, 0, DateTimeKind.Utc);
        }

        private static SyncStateEntry DirectoryState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            };
        }

        private static SyncStateEntry FileState(
            SyncPairSettings syncPair,
            string relativePath,
            long sizeBytes,
            DateTime lastWriteUtc)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = "baseline",
                LocalSizeBytes = sizeBytes,
                LocalLastWriteUtc = lastWriteUtc,
                RemoteFileId = Guid.NewGuid(),
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
            };
        }

        private static void AddDirectory(LocalTreeLookupSnapshot snapshot, string relativePath)
        {
            snapshot.DirectoriesByPath.Add(
                SyncPath.ToKey(relativePath),
                new LocalDirectorySnapshot
                {
                    RelativePath = relativePath,
                    FullPath = Path.Combine("C:\\Cloud", relativePath.Replace('/', '\\')),
                });
        }

        private static void AddFile(
            LocalTreeLookupSnapshot snapshot,
            string relativePath,
            long sizeBytes,
            DateTime lastWriteUtc,
            bool isCloudFilesPlaceholder = false,
            bool isOnlineOnly = false)
        {
            snapshot.FilesByPath.Add(
                SyncPath.ToKey(relativePath),
                new LocalFileSnapshot
                {
                    RelativePath = relativePath,
                    FullPath = Path.Combine("C:\\Cloud", relativePath.Replace('/', '\\')),
                    SizeBytes = sizeBytes,
                    LastWriteUtc = lastWriteUtc,
                    IsCloudFilesPlaceholder = isCloudFilesPlaceholder,
                    IsCloudFilesOnlineOnlyPlaceholder = isOnlineOnly,
                });
        }

        private static async Task AddCompletedCursorAsync(
            ISyncStateStore store,
            SyncPairSettings syncPair)
        {
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString("D"),
                HasCompletedFullReconcile = true,
            });
        }

        private static async Task WithStateStoreAsync(Func<ISyncStateStore, Task> test)
        {
            string databasePath = Path.Combine(
                Path.GetTempPath(),
                "cotton-offline-change-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new SqliteSyncStateStore(databasePath);
                await store.InitializeAsync();
                await test(store);
            }
            finally
            {
                DeleteIfExists(databasePath);
                DeleteIfExists(databasePath + "-shm");
                DeleteIfExists(databasePath + "-wal");
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private class FakeMetadataScanner : ILocalFileMetadataTreeLookupScanner
        {
            public LocalTreeLookupSnapshot Snapshot { get; } = new();

            public int ScanCallCount { get; private set; }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tree = new LocalTreeSnapshot();
                tree.Directories.AddRange(Snapshot.DirectoriesByPath.Values);
                tree.Files.AddRange(Snapshot.FilesByPath.Values);
                return Task.FromResult(tree);
            }

            public Task<LocalTreeLookupSnapshot> ScanTreeMetadataLookupsAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanCallCount++;
                return Task.FromResult(Snapshot);
            }
        }

        private class FakeProviderFileMarker : ILocalProviderFileMarker
        {
            private readonly bool _isUnchanged;

            public FakeProviderFileMarker(bool isUnchanged)
            {
                _isUnchanged = isUnchanged;
            }

            public List<string> InspectedPaths { get; } = [];

            public Task MarkAsync(
                Guid syncPairId,
                string localRootPath,
                string relativePath,
                string contentHash,
                long sizeBytes,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> IsUnchangedAsync(
                Guid syncPairId,
                string localRootPath,
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InspectedPaths.Add(localFile.RelativePath);
                return Task.FromResult(_isUnchanged);
            }
        }
    }
}
