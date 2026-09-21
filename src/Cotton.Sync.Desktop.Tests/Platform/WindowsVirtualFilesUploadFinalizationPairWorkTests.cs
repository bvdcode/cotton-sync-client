// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
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
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithMergedFullRequestPublishesFullProgressWithoutRequestedPaths()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt");
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingRunProgressPublisher progressPublisher = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(
                inner,
                activityPublisher,
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
        public async Task RunOnceAsync_WithFullMirrorUploadedActivityDoesNotTouchCloudFiles()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.FullMirror);
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
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
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter
            {
                Exception = new InvalidOperationException("Cloud Files status was not finalized."),
            };
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            RecordingRunProgressPublisher progressPublisher = new RecordingRunProgressPublisher();
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles,
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
                Assert.That(exception?.Message, Is.EqualTo("Cloud Files status was not finalized."));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(progressPublisher.Progress.Last().IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task SyncPairRunner_WhenCloudFilesDirectoryRepairFailsDoesNotReportIdleSuccess()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs/report.txt");
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter
            {
                DirectoryException = new InvalidOperationException("Cloud Files directory status was not finalized."),
            };
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles);
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

        private record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private class PublishingSyncPairWork : ISyncPairWork
        {
            private readonly IAppActivityPublisher _activityPublisher;
            private readonly SyncActivityKind _activityKind;
            private readonly string _uploadedPath;

            public PublishingSyncPairWork(
                IAppActivityPublisher activityPublisher,
                string uploadedPath,
                SyncActivityKind activityKind = SyncActivityKind.Uploaded)
            {
                _activityPublisher = activityPublisher;
                _uploadedPath = uploadedPath;
                _activityKind = activityKind;
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
                    _activityKind,
                    _uploadedPath,
                    "Uploaded " + _uploadedPath,
                    DateTime.UtcNow));
                return Task.CompletedTask;
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

            public Task<RemoteFilePlaceholderResult> FinalizeUploadedFilePlaceholderAsync(
                SyncPairSettings syncPair,
                SyncStateEntry fileState,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetInSyncState(syncPair, fileState.RelativePath);
                return Task.FromResult(new RemoteFilePlaceholderResult(
                    [1, 2, 3],
                    SyncPlaceholderHydrationState.Hydrated,
                    LocalSizeBytes: 25,
                    LocalLastWriteUtc: new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)));
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

        private partial class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public List<SuppressedWrite> MetadataSuppressedWrites { get; } = [];

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath)
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
