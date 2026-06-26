// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsVirtualFilesDehydrationPairWorkTests
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        [Test]
        public async Task RunOnceAsync_HydratesPinnedRemoteOnlyPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            int diskReads = 0;
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => diskReads++ == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/report.txt") }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-always-keep"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_DehydratesSafeUnpinnedPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => CreateUnpinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/report.txt") }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                Assert.That(updated.LocalContentHash, Is.Null);
                Assert.That(updated.LocalLastWriteUtc, Is.Null);
                Assert.That(updated.LocalSizeBytes, Is.Null);
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-free-up-space"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesPathToInnerSyncWhenContentHashDiffers()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-hash"),
                readDiskState: _ => CreateUnpinnedHydratedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.False);
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_RemovesHandledPathsAndSyncsRemainingPaths()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase)
                    ? CreateUnpinnedHydratedDiskState()
                    : null);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt", "Docs/edited.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.DehydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/edited.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesFullRequestsThrough()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                new FakeSyncStateStore(),
                new FakeCloudFilesAdapter());

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
            });
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributeUnpinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedRemoteOnlyDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | FileAttributes.Offline
                | (FileAttributes)FileAttributePinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static SyncPairSettings CreateVirtualFilesPair()
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Desktop",
                LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-root"),
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                RemoteDisplayPath = "/Desktop",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }

        private static SyncStateEntry CreatePlaceholderState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RemoteNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                RemoteContentHash = "remote-hash",
                RemoteSizeBytes = 12,
                PlaceholderIdentity = [1, 2, 3],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
            };
        }

        private sealed class RecordingSyncPairWork : ISyncPairWork
        {
            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                Requests.Add(SyncRunRequest.Full);
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeContentHasher : ILocalFileContentHasher
        {
            private readonly string _hash;

            public FakeContentHasher(string hash)
            {
                _hash = hash;
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_hash);
            }
        }

        private sealed record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private sealed class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                return false;
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<string> DehydratedPaths { get; } = [];

            public List<string> HydratedPaths { get; } = [];

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                DehydratedPaths.Add(relativePath);
            }

            public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                HydratedPaths.Add(relativePath);
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public void UpsertEntry(SyncStateEntry entry)
            {
                _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
            }

            public SyncStateEntry GetRequired(Guid syncPairId, string relativePath)
            {
                return _entries[CreateKey(syncPairId.ToString("D"), relativePath)];
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncStateEntry> entries = _entries.Values
                    .Where(entry => string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return Task.FromResult(entries);
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry;
                    }
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(CreateKey(syncPairId, relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                UpsertEntry(entry);
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.Remove(CreateKey(syncPairId, relativePath));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                foreach (SyncStateEntry entry in entries)
                {
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            private static string CreateKey(string syncPairId, string relativePath)
            {
                return syncPairId.ToUpperInvariant() + "|" + SyncPath.ToKey(relativePath);
            }
        }
    }
}
