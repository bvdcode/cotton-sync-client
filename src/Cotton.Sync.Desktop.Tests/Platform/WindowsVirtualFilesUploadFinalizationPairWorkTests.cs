// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
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
            var cloudFiles = new RecordingCloudFilesAdapter();
            var suppression = new RecordingLocalChangeSuppression();
            var work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                cloudFiles,
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.InSyncPaths,
                    Is.EqualTo(new[] { "Docs/Reports/report.txt", "Docs", "Docs/Reports" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports/report.txt"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports"),
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

            public Exception? Exception { get; init; }

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

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                InSyncPaths.Add(relativePath);
                if (Exception is not null)
                {
                    throw Exception;
                }
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
