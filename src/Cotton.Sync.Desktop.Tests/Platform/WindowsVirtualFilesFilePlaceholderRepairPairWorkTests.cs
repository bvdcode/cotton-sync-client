// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesFilePlaceholderRepairPairWorkTests
    {
        private string _localRootPath = null!;

        [SetUp]
        public void SetUp()
        {
            _localRootPath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "file-placeholder-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_localRootPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_localRootPath))
            {
                Directory.Delete(_localRootPath, recursive: true);
            }
        }

        [Test]
        public async Task RunOnceAsync_WithFullVfsRunRepairsOnlyTrackedPlaceholderWithoutInSyncState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            WriteFile("Music/stale.mp3");
            WriteFile("Music/current.mp3");
            WriteFile("Music/local.mp3");
            FakeSyncStateStore stateStore = new FakeSyncStateStore(
                CreateFileState(syncPair, "Music/stale.mp3", trackedPlaceholder: true),
                CreateFileState(syncPair, "Music/current.mp3", trackedPlaceholder: true),
                CreateFileState(syncPair, "Music/missing.mp3", trackedPlaceholder: true),
                CreateFileState(syncPair, "Music/local.mp3", trackedPlaceholder: false));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            cloudFiles.States["Music/stale.mp3"] = WindowsCloudFilesPlaceholderState.Placeholder;
            cloudFiles.States["Music/current.mp3"] =
                WindowsCloudFilesPlaceholderState.Placeholder | WindowsCloudFilesPlaceholderState.InSync;
            cloudFiles.Identities["Music/current.mp3"] = [9, 8, 7];
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingRunProgressPublisher progress = new RecordingRunProgressPublisher();
            WindowsVirtualFilesFilePlaceholderRepairPairWork work = new WindowsVirtualFilesFilePlaceholderRepairPairWork(
                inner,
                stateStore,
                cloudFiles,
                diagnostics: diagnostics,
                runProgressPublisher: progress);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Resume | SyncRunCause.InitialPopulation));

            WindowsCloudFilesDiagnosticEvent repairEvent = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.InspectedPaths,
                    Is.EqualTo(new[] { "Music/current.mp3", "Music/stale.mp3" }));
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Music/stale.mp3" }));
                Assert.That(cloudFiles.IdentityUpdatedPaths, Is.EqualTo(new[] { "Music/current.mp3" }));
                Assert.That(cloudFiles.SyncRootInSyncCount, Is.EqualTo(1));
                Assert.That(repairEvent.Operation, Is.EqualTo("repair-file-placeholder-in-sync"));
                Assert.That(repairEvent.Status, Is.EqualTo("completed"));
                Assert.That(repairEvent.Details, Does.Contain("candidates=3"));
                Assert.That(repairEvent.Details, Does.Contain("repaired=2"));
                Assert.That(repairEvent.Details, Does.Contain("missing=1"));
                Assert.That(repairEvent.Details, Does.Contain("non-placeholders=0"));
                Assert.That(progress.Progress.First().FilesTotal, Is.Null);
                Assert.That(progress.Progress.Last().FilesCompleted, Is.EqualTo(3));
                Assert.That(progress.Progress.Last().FilesTotal, Is.EqualTo(3));
                Assert.That(progress.Progress.Last().IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedVfsRunDoesNotScanTrackedPlaceholders()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            WriteFile("Music/stale.mp3");
            FakeSyncStateStore stateStore = new FakeSyncStateStore(
                CreateFileState(syncPair, "Music/stale.mp3", trackedPlaceholder: true));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsVirtualFilesFilePlaceholderRepairPairWork work = new WindowsVirtualFilesFilePlaceholderRepairPairWork(
                inner,
                stateStore,
                cloudFiles,
                diagnostics: diagnostics);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music/stale.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(cloudFiles.InspectedPaths, Is.Empty);
                Assert.That(diagnostics.Snapshot(), Is.Empty);
            });
        }

        private SyncPairSettings CreateSyncPair(SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Cloud",
                LocalRootPath = _localRootPath,
                RemoteDisplayPath = "/",
                RemoteRootNodeId = Guid.NewGuid(),
                Mode = mode,
                IsEnabled = true,
            };
        }

        private static SyncStateEntry CreateFileState(
            SyncPairSettings syncPair,
            string relativePath,
            bool trackedPlaceholder)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
                PlaceholderIdentity = trackedPlaceholder ? [1, 2, 3] : null,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private void WriteFile(string relativePath)
        {
            string fullPath = Path.Combine(
                _localRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "test");
        }

        private class RecordingSyncPairWork : ISyncPairWork
        {
            public List<SyncRunRequest> Requests { get; } = [];

            public Func<Task>? OnRunAsync { get; set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (OnRunAsync is not null)
                {
                    await OnRunAsync().ConfigureAwait(false);
                }
            }
        }

        private class RecordingCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public Dictionary<string, WindowsCloudFilesPlaceholderState> States { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public List<string> InspectedPaths { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public Dictionary<string, byte[]> Identities { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public List<string> IdentityUpdatedPaths { get; } = [];

            public int SyncRootInSyncCount { get; private set; }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(
                SyncPairSettings syncPair,
                string? relativePath = null)
            {
                string path = SyncPath.Normalize(relativePath!);
                InspectedPaths.Add(path);
                return States[path];
            }

            public byte[] GetPlaceholderIdentity(SyncPairSettings syncPair, string relativePath)
            {
                string path = SyncPath.Normalize(relativePath);
                return Identities.TryGetValue(path, out byte[]? identity)
                    ? identity
                    : [1, 2, 3];
            }

            public void UpdatePlaceholderIdentity(
                SyncPairSettings syncPair,
                string relativePath,
                byte[] placeholderIdentity)
            {
                string path = SyncPath.Normalize(relativePath);
                IdentityUpdatedPaths.Add(path);
                Identities[path] = placeholderIdentity;
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                string path = SyncPath.Normalize(relativePath);
                InSyncPaths.Add(path);
                States[path] |= WindowsCloudFilesPlaceholderState.InSync;
            }

            public void SetSyncRootInSyncState(SyncPairSettings syncPair)
            {
                SyncRootInSyncCount++;
            }

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

        private class RecordingRunProgressPublisher : IAppRunProgressPublisher
        {
            public List<AppRunProgress> Progress { get; } = [];

            public IDisposable Subscribe(IObserver<AppRunProgress> observer)
            {
                throw new NotSupportedException();
            }

            public void Publish(AppRunProgress progress)
            {
                Progress.Add(progress);
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly List<SyncStateEntry> _entries;

            public FakeSyncStateStore(params SyncStateEntry[] entries)
            {
                _entries = [.. entries];
            }

            public int LoadPairEntriesCallCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>(
                    _entries.Where(entry => entry.SyncPairId == syncPairId).ToArray());
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCallCount++;
                foreach (SyncStateEntry entry in _entries
                             .Where(entry => entry.SyncPairId == syncPairId)
                             .OrderBy(entry => SyncPath.ToKey(entry.RelativePath), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
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
                return Task.FromResult(_entries.SingleOrDefault(entry =>
                    entry.SyncPairId == syncPairId
                    && string.Equals(entry.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(existing =>
                    existing.SyncPairId == entry.SyncPairId
                    && string.Equals(existing.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
                _entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(
                SyncChangeCursor cursor,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(entry =>
                    entry.SyncPairId == syncPairId
                    && string.Equals(entry.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(entry => entry.SyncPairId == syncPairId);
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(entry => entry.SyncPairId == syncPairId);
                _entries.AddRange(entries);
                return Task.CompletedTask;
            }
        }
    }
}
