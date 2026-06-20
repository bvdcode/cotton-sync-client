// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadedActivityMarksCloudFilesPathInSync()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var inner = new PublishingSyncPairWork(activityPublisher, "Docs/Reports/report.txt");
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            stateStore.UpsertDirectory(syncPair, "Docs/Reports", Guid.Parse("44444444-4444-4444-4444-444444444444"));
            var cloudFiles = new RecordingCloudFilesAdapter();
            var suppression = new RecordingLocalChangeSuppression();
            var work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles,
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.InSyncPaths,
                    Is.EqualTo(new[] { "Docs/Reports/report.txt" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs/Reports", "Docs" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RemoteDirectory.Id),
                    Is.EqualTo(new[]
                    {
                        Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    }));
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports/report.txt"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs"),
                    }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithFullMirrorUploadedActivityDoesNotTouchCloudFiles()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.FullMirror);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            var cloudFiles = new RecordingCloudFilesAdapter();
            var work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                new FakeSyncStateStore(),
                cloudFiles);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
            });
        }

        [Test]
        public async Task SyncPairRunner_WhenCloudFilesFinalizationFailsDoesNotReportIdleSuccess()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            var cloudFiles = new RecordingCloudFilesAdapter
            {
                Exception = new InvalidOperationException("Cloud Files status was not finalized."),
            };
            var work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                new FakeSyncStateStore(),
                cloudFiles);
            var runner = new SyncPairRunner(
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
                Assert.That(exception?.Message, Is.EqualTo("Cloud Files status was not finalized."));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
            });
        }

        [Test]
        public async Task SyncPairRunner_WhenCloudFilesDirectoryRepairFailsDoesNotReportIdleSuccess()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var activityPublisher = new InMemoryAppActivityPublisher();
            var inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            var cloudFiles = new RecordingCloudFilesAdapter
            {
                DirectoryException = new InvalidOperationException("Cloud Files directory status was not finalized."),
            };
            var work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles);
            var runner = new SyncPairRunner(
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
                Assert.That(exception?.Message, Is.EqualTo("Cloud Files directory status was not finalized."));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath), Is.EqualTo(new[] { "Docs" }));
            });
        }

        private static SyncPairSettings CreateSyncPair(SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Desktop",
                LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-upload-finalization"),
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                RemoteDisplayPath = "/Desktop",
                IsEnabled = true,
                Mode = mode,
            };
        }

        private sealed record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private class PublishingSyncPairWork : ISyncPairWork
        {
            private readonly IAppActivityPublisher _activityPublisher;
            private readonly string _uploadedPath;

            public PublishingSyncPairWork(IAppActivityPublisher activityPublisher, string uploadedPath)
            {
                _activityPublisher = activityPublisher;
                _uploadedPath = uploadedPath;
            }

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
                _activityPublisher.Publish(new AppSyncActivity(
                    Guid.NewGuid(),
                    syncPair.Id,
                    SyncActivityKind.Uploaded,
                    _uploadedPath,
                    "Uploaded " + _uploadedPath,
                    DateTime.UtcNow));
                return Task.CompletedTask;
            }
        }

        private class RecordingCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<string> InSyncPaths { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> DirectoryPlaceholders { get; } = [];

            public List<SyncPairSettings> SyncRootInSyncPairs { get; } = [];

            public Exception? Exception { get; init; }

            public Exception? DirectoryException { get; init; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
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
                InSyncPaths.Add(relativePath);
                if (Exception is not null)
                {
                    throw Exception;
                }
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
                foreach (SyncStateEntry entry in _entries.Values.Where(entry => entry.SyncPairId == syncPairId))
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

        private class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
