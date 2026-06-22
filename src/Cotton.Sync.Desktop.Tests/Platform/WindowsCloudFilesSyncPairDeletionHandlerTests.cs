// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesSyncPairDeletionHandlerTests
    {
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-vfs-delete-" + Guid.NewGuid().ToString("N"));
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
        public async Task BeforeDeleteAsync_UnregistersWindowsVirtualFilesSyncRoot()
        {
            var adapter = new FakeCloudFilesAdapter();
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter);
            SyncPairSettings syncPair = CreatePair(SyncPairMode.WindowsVirtualFiles);

            await handler.BeforeDeleteAsync(syncPair);

            Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { syncPair.Id }));
        }

        [Test]
        public async Task BeforeDeleteAsync_CleansSafeLocalRootAfterUnregister()
        {
            var operations = new List<string>();
            var adapter = new FakeCloudFilesAdapter(operations);
            var cleaner = new FakeRootCleaner(operations, shouldRemoveRoot: true);
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter, rootCleaner: cleaner);
            SyncPairSettings syncPair = CreatePair(SyncPairMode.WindowsVirtualFiles);

            await handler.BeforeDeleteAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(operations, Is.EqualTo(new[] { "evaluate", "unregister", "cleanup" }));
                Assert.That(cleaner.EvaluatedPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(cleaner.CleanupDecisions.Select(static decision => decision.LocalRootPath), Is.EqualTo(new[] { syncPair.LocalRootPath }));
            });
        }

        [Test]
        public async Task BeforeDeleteAsync_PreservesLocalRootWhenCleanerDeclines()
        {
            var operations = new List<string>();
            var adapter = new FakeCloudFilesAdapter(operations);
            var cleaner = new FakeRootCleaner(operations, shouldRemoveRoot: false);
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter, rootCleaner: cleaner);
            SyncPairSettings syncPair = CreatePair(SyncPairMode.WindowsVirtualFiles);

            await handler.BeforeDeleteAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(cleaner.CleanupResults.Select(static result => result.RootRemoved), Is.EqualTo(new[] { false }));
            });
        }

        [Test]
        public async Task BeforeDeleteAsync_SkipsFullMirrorSyncPair()
        {
            var adapter = new FakeCloudFilesAdapter();
            var cleaner = new FakeRootCleaner([], shouldRemoveRoot: true);
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter, rootCleaner: cleaner);

            await handler.BeforeDeleteAsync(CreatePair(SyncPairMode.FullMirror));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.UnregisteredPairs, Is.Empty);
                Assert.That(cleaner.EvaluatedPairs, Is.Empty);
                Assert.That(cleaner.CleanupDecisions, Is.Empty);
            });
        }

        [Test]
        public void BeforeDeleteAsync_HonorsCancellationBeforeNativeCleanup()
        {
            var adapter = new FakeCloudFilesAdapter();
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.That(
                async () => await handler.BeforeDeleteAsync(CreatePair(SyncPairMode.WindowsVirtualFiles), cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(adapter.UnregisteredPairs, Is.Empty);
        }

        [Test]
        public void WindowsVirtualFilesRootCleaner_SkipsRegularLocalFile()
        {
            string rootPath = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(rootPath);
            File.WriteAllText(Path.Combine(rootPath, "local-only.txt"), "local");
            var cleaner = new WindowsVirtualFilesRootCleaner();

            WindowsVirtualFilesRootCleanupDecision decision =
                cleaner.EvaluateBeforeUnregister(CreatePair(SyncPairMode.WindowsVirtualFiles, rootPath));

            Assert.Multiple(() =>
            {
                Assert.That(decision.ShouldRemoveRoot, Is.False);
                Assert.That(decision.Reason, Does.Contain("regular local file"));
            });
        }

        [Test]
        public void WindowsVirtualFilesRootCleaner_TreatsOnlineOnlyAttributesAsSafePlaceholder()
        {
            FileAttributes onlineOnlyAttributes = FileAttributes.Archive
                | (FileAttributes)FileAttributeUnpinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            FileAttributes recallOnOpenAttributes = FileAttributes.Archive
                | (FileAttributes)FileAttributeUnpinned
                | (FileAttributes)FileAttributeRecallOnOpen;
            FileAttributes offlineAttributes = FileAttributes.Archive
                | (FileAttributes)FileAttributeUnpinned
                | FileAttributes.Offline;

            bool safe = WindowsVirtualFilesRootCleaner.IsSafeCloudFilesPlaceholder(onlineOnlyAttributes);
            bool recallOnOpenSafe = WindowsVirtualFilesRootCleaner.IsSafeCloudFilesPlaceholder(recallOnOpenAttributes);
            bool offlineSafe = WindowsVirtualFilesRootCleaner.IsSafeCloudFilesPlaceholder(offlineAttributes);
            bool regularFileSafe = WindowsVirtualFilesRootCleaner.IsSafeCloudFilesPlaceholder(FileAttributes.Archive);
            bool reparsePointSafe = WindowsVirtualFilesRootCleaner.IsSafeCloudFilesPlaceholder(FileAttributes.ReparsePoint);

            Assert.Multiple(() =>
            {
                Assert.That(safe, Is.True);
                Assert.That(recallOnOpenSafe, Is.True);
                Assert.That(offlineSafe, Is.True);
                Assert.That(regularFileSafe, Is.False);
                Assert.That(reparsePointSafe, Is.False);
            });
        }

        [Test]
        public async Task WindowsVirtualFilesRootCleaner_RemovesEmptyRootWhenDecisionAllows()
        {
            string rootPath = Path.Combine(_tempDirectory, "empty-root");
            Directory.CreateDirectory(Path.Combine(rootPath, "top-level"));
            var cleaner = new WindowsVirtualFilesRootCleaner();
            WindowsVirtualFilesRootCleanupDecision decision =
                cleaner.EvaluateBeforeUnregister(CreatePair(SyncPairMode.WindowsVirtualFiles, rootPath));

            WindowsVirtualFilesRootCleanupResult result =
                await cleaner.CleanupAfterUnregisterAsync(decision);

            Assert.Multiple(() =>
            {
                Assert.That(decision.ShouldRemoveRoot, Is.True);
                Assert.That(result.RootRemoved, Is.True);
                Assert.That(Directory.Exists(rootPath), Is.False);
            });
        }

        [Test]
        public async Task WindowsVirtualFilesRootCleaner_PreservesRootWhenRegularFileAppearsBeforeCleanup()
        {
            string rootPath = Path.Combine(_tempDirectory, "changed-root");
            Directory.CreateDirectory(rootPath);
            var cleaner = new WindowsVirtualFilesRootCleaner();
            WindowsVirtualFilesRootCleanupDecision decision =
                cleaner.EvaluateBeforeUnregister(CreatePair(SyncPairMode.WindowsVirtualFiles, rootPath));
            File.WriteAllText(Path.Combine(rootPath, "local-after-evaluate.txt"), "local");

            WindowsVirtualFilesRootCleanupResult result =
                await cleaner.CleanupAfterUnregisterAsync(decision);

            Assert.Multiple(() =>
            {
                Assert.That(decision.ShouldRemoveRoot, Is.True);
                Assert.That(result.RootRemoved, Is.False);
                Assert.That(result.Details, Does.Contain("changed before cleanup"));
                Assert.That(Directory.Exists(rootPath), Is.True);
            });
        }

        private static SyncPairSettings CreatePair(SyncPairMode mode, string? localRootPath = null)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = localRootPath ?? @"S:\CottonSyncVfsQa\root",
                RemoteDisplayPath = "/Documents",
                RemoteRootNodeId = Guid.NewGuid(),
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            private readonly List<string>? _operations;

            public FakeCloudFilesAdapter(List<string>? operations = null)
            {
                _operations = operations;
            }

            public List<SyncPairSettings> UnregisteredPairs { get; } = [];

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                _operations?.Add("unregister");
                UnregisteredPairs.Add(syncPair);
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
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

        private sealed class FakeRootCleaner : IWindowsVirtualFilesRootCleaner
        {
            private readonly List<string> _operations;
            private readonly bool _shouldRemoveRoot;

            public FakeRootCleaner(List<string> operations, bool shouldRemoveRoot)
            {
                _operations = operations;
                _shouldRemoveRoot = shouldRemoveRoot;
            }

            public List<SyncPairSettings> EvaluatedPairs { get; } = [];

            public List<WindowsVirtualFilesRootCleanupDecision> CleanupDecisions { get; } = [];

            public List<WindowsVirtualFilesRootCleanupResult> CleanupResults { get; } = [];

            public WindowsVirtualFilesRootCleanupDecision EvaluateBeforeUnregister(SyncPairSettings syncPair)
            {
                _operations.Add("evaluate");
                EvaluatedPairs.Add(syncPair);
                return new WindowsVirtualFilesRootCleanupDecision(
                    syncPair.LocalRootPath,
                    _shouldRemoveRoot,
                    _shouldRemoveRoot ? "clean" : "regular file");
            }

            public Task<WindowsVirtualFilesRootCleanupResult> CleanupAfterUnregisterAsync(
                WindowsVirtualFilesRootCleanupDecision decision,
                CancellationToken cancellationToken = default)
            {
                _operations.Add("cleanup");
                CleanupDecisions.Add(decision);
                var result = new WindowsVirtualFilesRootCleanupResult(
                    decision.ShouldRemoveRoot,
                    decision.ShouldRemoveRoot ? "removed" : "preserved");
                CleanupResults.Add(result);
                return Task.FromResult(result);
            }
        }
    }
}
