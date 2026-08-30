// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDirectoryPlaceholderRepairPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRootOnlyRepairPublishesFinalizingProgress()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingRunProgressPublisher progressPublisher = new RecordingRunProgressPublisher();
            WindowsVirtualFilesDirectoryPlaceholderRepairPairWork work = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                inner,
                stateStore,
                cloudFiles,
                diagnostics: diagnostics,
                runProgressPublisher: progressPublisher);

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            WindowsCloudFilesDiagnosticEvent repairEvent = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(cloudFiles.DirectoryPlaceholders, Is.Empty);
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(repairEvent.Status, Is.EqualTo("completed-root-only"));
                Assert.That(
                    progressPublisher.Progress.Select(static progress => new
                    {
                        progress.Stage,
                        progress.FilesCompleted,
                        progress.FilesTotal,
                        progress.IsCompleted,
                    }),
                    Is.EqualTo(new[]
                    {
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)1, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)1, IsCompleted = true },
                    }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithMergedFullRequestPublishesFullProgressWithoutRequestedPaths()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            RecordingSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingRunProgressPublisher progressPublisher = new();
            WindowsVirtualFilesDirectoryPlaceholderRepairPairWork work = new(
                inner,
                stateStore,
                new RecordingCloudFilesAdapter(),
                runProgressPublisher: progressPublisher);
            SyncRunRequest request = SyncRunRequest
                .ForLocalChangedPaths(["Docs/report.txt"])
                .Merge(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(progressPublisher.Progress, Is.Not.Empty);
                Assert.That(progressPublisher.Progress.Select(static progress => progress.IsFull), Is.All.EqualTo(true));
                Assert.That(progressPublisher.Progress.Select(static progress => progress.RequestedPathCount), Is.All.EqualTo(0));
            });
        }

        [Test]
        public async Task SyncPairRunner_WhenDirectoryRepairFailsDoesNotReportIdleSuccess()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter
            {
                DirectoryException = new InvalidOperationException("Cloud Files directory repair failed."),
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingRunProgressPublisher progressPublisher = new RecordingRunProgressPublisher();
            WindowsVirtualFilesDirectoryPlaceholderRepairPairWork work = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                new RecordingSyncPairWork(),
                stateStore,
                cloudFiles,
                diagnostics: diagnostics,
                runProgressPublisher: progressPublisher);
            SyncPairRunner runner = new SyncPairRunner(
                syncPair,
                work,
                new SyncPairRunnerRetryOptions
                {
                    MaxAttempts = 1,
                });

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Is.EqualTo("Cloud Files directory repair failed."));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
                Assert.That(cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath), Is.EqualTo(new[] { "Docs" }));
                Assert.That(diagnostics.Snapshot().Single().Status, Is.EqualTo("failed"));
                Assert.That(progressPublisher.Progress.Last().IsCompleted, Is.True);
            });
        }

        private static SyncPairSettings CreateSyncPair(SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Desktop",
                LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-directory-repair"),
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                RemoteDisplayPath = "/Desktop",
                IsEnabled = true,
                Mode = mode,
            };
        }

        private record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

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

        private class RecordingSyncPairWork : ISyncPairWork
        {
            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
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

        private class RecordingCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<RemoteDirectoryMaterializationRequest> DirectoryPlaceholders { get; } = [];

            public List<SyncPairSettings> SyncRootInSyncPairs { get; } = [];

            public Exception? DirectoryException { get; init; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public IReadOnlyList<RemoteFilePlaceholderResult> CreateFilePlaceholders(IReadOnlyList<RemoteFilePlaceholderRequest> requests)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void CreateDirectoryPlaceholder(RemoteDirectoryMaterializationRequest request)
            {
                DirectoryPlaceholders.Add(request);
                if (DirectoryException is not null)
                {
                    throw DirectoryException;
                }
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public void SetSyncRootInSyncState(SyncPairSettings syncPair)
            {
                SyncRootInSyncPairs.Add(syncPair);
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

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public void UpsertDirectory(SyncPairSettings syncPair, string relativePath, Guid remoteNodeId)
            {
                _entries[CreateKey(syncPair.Id.ToString("D"), relativePath)] = new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = remoteNodeId,
                    SyncedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                };
            }

            public void UpsertFile(SyncPairSettings syncPair, string relativePath, Guid remoteNodeId)
            {
                _entries[CreateKey(syncPair.Id.ToString("D"), relativePath)] = new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = remoteNodeId,
                    RemoteFileId = Guid.NewGuid(),
                    SyncedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                };
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>(
                    _entries.Values.Where(entry => entry.SyncPairId == syncPairId).ToArray());
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values
                             .Where(entry => entry.SyncPairId == syncPairId)
                             .OrderBy(entry => SyncPath.ToKey(entry.RelativePath), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairDirectoryEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values
                             .Where(entry => entry.SyncPairId == syncPairId && entry.Kind == SyncEntryKind.Directory)
                             .OrderBy(entry => SyncPath.ToKey(entry.RelativePath), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadDirectoryEntriesByPathPrefixAsync(
                string syncPairId,
                string relativePathPrefix,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                string prefixKey = SyncPath.ToKey(relativePathPrefix);
                string childPrefix = prefixKey + "/";
                foreach (SyncStateEntry entry in _entries.Values
                             .Where(entry => entry.SyncPairId == syncPairId
                                 && entry.Kind == SyncEntryKind.Directory
                                 && (SyncPath.ToKey(entry.RelativePath).Equals(prefixKey, StringComparison.OrdinalIgnoreCase)
                                     || SyncPath.ToKey(entry.RelativePath).StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)))
                             .OrderBy(entry => SyncPath.ToKey(entry.RelativePath), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(CreateKey(syncPairId, relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
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
                foreach (string key in _entries.Values
                             .Where(entry => entry.SyncPairId == syncPairId)
                             .Select(entry => CreateKey(entry.SyncPairId, entry.RelativePath))
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
                _ = DeletePairAsync(syncPairId, cancellationToken);
                foreach (SyncStateEntry entry in entries)
                {
                    _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
                }

                return Task.CompletedTask;
            }

            private static string CreateKey(string syncPairId, string relativePath)
            {
                return syncPairId + "|" + SyncPath.ToKey(relativePath);
            }
        }

        private class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public List<SuppressedWrite> MetadataSuppressedWrites { get; } = [];

            public List<string> BurstSuppressedRoots { get; } = [];

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderDirectoryWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public void SuppressProviderMetadataWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                MetadataSuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                BurstSuppressedRoots.Add(localRootPath);
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                return false;
            }
        }

        private class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
