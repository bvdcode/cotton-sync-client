// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Cotton.Files;
using Cotton.Nodes;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class DesktopCloudFilesPlaceholderWriterTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-placeholder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void CreatePlaceholderAsync_RejectsUnsupportedCloudFilesHost()
        {
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(false, "Cloud Files is disabled."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory)));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Is.EqualTo("Cloud Files is disabled."));
            });
        }

        [Test]
        public void CreatePlaceholderAsync_RejectsUnsafeRootBeforeNativeCall()
        {
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                new WindowsVirtualFilesRootSafetyPolicy(
                    folder => folder == Environment.SpecialFolder.UserProfile ? @"C:\Users\Example" : string.Empty,
                    () => _tempDirectory),
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(@"C:\Users\Example")));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Does.Contain("user profile"));
            });
        }

        [Test]
        public async Task CreatePlaceholderAsync_CreatesPlaceholderThroughAdapter()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderResult result = await writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.Requests, Has.Count.EqualTo(1));
                Assert.That(adapter.Requests[0].RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(result.PlaceholderIdentity, Is.EqualTo(adapter.PlaceholderIdentity));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task CreatePlaceholderAsync_SuppressesLocalWatcherEventsBeforeAdapterCall()
        {
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter
            {
                OnCreate = _ => Assert.That(suppression.SuppressedWrites, Has.Count.EqualTo(1)),
            };
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                localChangeSuppression: suppression,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");

            await writer.CreatePlaceholderAsync(CreateRequest(
                _tempDirectory,
                syncPairId.ToString("D"),
                "Projects/report.txt"));

            Assert.That(
                suppression.SuppressedWrites,
                Is.EqualTo(new[] { new SuppressedWrite(syncPairId, _tempDirectory, "Projects/report.txt") }));
        }

        [Test]
        public async Task CreatePlaceholdersAsync_SuppressesLocalWatcherEventsBeforeBatchAdapterCall()
        {
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter
            {
                OnCreateBatch = _ => Assert.That(suppression.SuppressedWrites, Has.Count.EqualTo(2)),
            };
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                localChangeSuppression: suppression,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");

            IReadOnlyList<RemoteFilePlaceholderBatchResult> results = await writer.CreatePlaceholdersAsync(
            [
                CreateRequest(_tempDirectory, syncPairId.ToString("D"), "Projects/first.txt"),
                CreateRequest(_tempDirectory, syncPairId.ToString("D"), "Projects/second.txt"),
            ]);

            Assert.Multiple(() =>
            {
                Assert.That(adapter.BatchRequests, Has.Count.EqualTo(1));
                Assert.That(adapter.BatchRequests[0].Select(static request => request.RelativePath), Is.EqualTo(new[]
                {
                    "Projects/first.txt",
                    "Projects/second.txt",
                }));
                Assert.That(results.Select(static result => result.IsSuccess), Is.All.True);
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPairId, _tempDirectory, "Projects/first.txt"),
                        new SuppressedWrite(syncPairId, _tempDirectory, "Projects/second.txt"),
                    }));
            });
        }

        [Test]
        public void CreatePlaceholderAsync_StopsBeforeAdapterWhenCanceled()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(
                () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory), cancellation.Token));

            Assert.That(adapter.Requests, Is.Empty);
        }

        [Test]
        public void CreatePlaceholderAsync_ReportsAdapterFailureAsPlaceholderUnavailable()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter
            {
                Exception = new WindowsCloudFilesNativeException("CfCreatePlaceholders", unchecked((int)0x8007017C)),
            };
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory)));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.Requests, Has.Count.EqualTo(1));
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Does.Contain("CfCreatePlaceholders"));
            });
        }

        private RemoteDirectoryMaterializationRequest CreateDirectoryRequest(
            Guid syncPairId,
            Guid remoteRootNodeId,
            string relativePath)
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPairId.ToString("D"),
                _tempDirectory,
                remoteRootNodeId,
                relativePath,
                new NodeDto
                {
                    Id = Guid.NewGuid(),
                    Name = Path.GetFileName(relativePath),
                });
        }

        private static RemoteFilePlaceholderRequest CreateRequest(
            string localRootPath,
            string syncPairId = "pair-a",
            string relativePath = "remote-only.txt")
        {
            return new RemoteFilePlaceholderRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    NodeId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    FileManifestId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    OriginalNodeFileId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OwnerId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = 12,
                    ContentHash = "hash",
                    ETag = "etag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private record SuppressedFileMaterialization(
            Guid SyncPairId,
            string LocalRootPath,
            string RelativePath,
            long ExpectedSizeBytes,
            DateTime? ExpectedLastWriteUtc);

        private class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public List<SuppressedWrite> SuppressedFileCreations { get; } = [];

            public List<SuppressedFileMaterialization> SuppressedFileMaterializations { get; } = [];

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                throw new InvalidOperationException("Placeholder creation must use online-only watcher suppression.");
            }

            public void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                throw new InvalidOperationException("Placeholder creation must not use pinned watcher suppression.");
            }

            public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedFileCreations.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderFileMaterialization(
                Guid syncPairId,
                string localRootPath,
                string relativePath,
                long expectedSizeBytes,
                DateTime? expectedLastWriteUtc)
            {
                SuppressedFileMaterializations.Add(new SuppressedFileMaterialization(
                    syncPairId,
                    localRootPath,
                    relativePath,
                    expectedSizeBytes,
                    expectedLastWriteUtc));
            }

            public void SuppressProviderOnlineOnlyWrite(Guid syncPairId, string localRootPath, string relativePath)
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

        private class RecordingProviderFileMarker : ILocalProviderFileMarker
        {
            public Guid? SyncPairId { get; private set; }

            public string? LocalRootPath { get; private set; }

            public string? RelativePath { get; private set; }

            public string? ContentHash { get; private set; }

            public long? SizeBytes { get; private set; }

            public Task MarkAsync(
                Guid syncPairId,
                string localRootPath,
                string relativePath,
                string contentHash,
                long sizeBytes,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncPairId = syncPairId;
                LocalRootPath = localRootPath;
                RelativePath = relativePath;
                ContentHash = contentHash;
                SizeBytes = sizeBytes;
                return Task.CompletedTask;
            }

            public Task<bool> IsUnchangedAsync(
                Guid syncPairId,
                string localRootPath,
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public byte[] PlaceholderIdentity { get; } = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public List<IReadOnlyList<RemoteFilePlaceholderRequest>> BatchRequests { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> DirectoryPlaceholders { get; } = [];

            public List<SyncPairSettings> SyncRootInSyncPairs { get; } = [];

            public Exception? Exception { get; set; }

            public Action<RemoteFilePlaceholderRequest>? OnCreate { get; set; }

            public Action<IReadOnlyList<RemoteFilePlaceholderRequest>>? OnCreateBatch { get; set; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                OnCreate?.Invoke(request);
                Requests.Add(request);
                if (Exception is not null)
                {
                    throw Exception;
                }

                return new RemoteFilePlaceholderResult(PlaceholderIdentity);
            }

            public IReadOnlyList<RemoteFilePlaceholderResult> CreateFilePlaceholders(
                IReadOnlyList<RemoteFilePlaceholderRequest> requests)
            {
                OnCreateBatch?.Invoke(requests);
                BatchRequests.Add(requests.ToArray());
                if (Exception is not null)
                {
                    throw Exception;
                }

                Requests.AddRange(requests);
                return requests
                    .Select(_ => new RemoteFilePlaceholderResult(PlaceholderIdentity))
                    .ToArray();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void CreateDirectoryPlaceholder(RemoteDirectoryMaterializationRequest request)
            {
                DirectoryPlaceholders.Add(request);
                if (Exception is not null)
                {
                    throw Exception;
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
    }
}
