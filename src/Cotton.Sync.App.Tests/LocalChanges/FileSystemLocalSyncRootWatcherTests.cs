// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public class FileSystemLocalSyncRootWatcherTests
    {
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-local-watcher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public void WatchedNotifyFilters_IncludeAttributesForCloudFilesPinState()
        {
            Assert.That(
                FileSystemLocalSyncRootWatcher.WatchedNotifyFilters.HasFlag(NotifyFilters.Attributes),
                Is.True);
        }

        [Test]
        public async Task StartAsync_RejectsMissingRoot()
        {
            string missingRoot = Path.Combine(_root, "missing");
            var watcher = new FileSystemLocalSyncRootWatcher(Guid.NewGuid(), missingRoot);

            DirectoryNotFoundException? exception = Assert.ThrowsAsync<DirectoryNotFoundException>(() => watcher.StartAsync());

            Assert.That(exception, Is.Not.Null);
            await watcher.DisposeAsync();
        }

        [Test]
        public async Task StartAsync_PublishesFileEvents()
        {
            Guid syncPairId = Guid.NewGuid();
            var watcher = new FileSystemLocalSyncRootWatcher(syncPairId, _root);
            var observed = new TaskCompletionSource<LocalSyncRootChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, change) => observed.TrySetResult(change);

            await watcher.StartAsync();
            string changedPath = Path.Combine(_root, "file.txt");
            File.WriteAllText(changedPath, "content");

            LocalSyncRootChange localChange = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watcher.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(localChange.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(localChange.FullPath, Is.EqualTo(changedPath));
                Assert.That(localChange.Kind, Is.AnyOf(LocalSyncRootChangeKind.Created, LocalSyncRootChangeKind.Changed));
            });
        }

        [Test]
        public async Task StartAsync_IgnoresCottonTemporaryDownloadEvents()
        {
            Guid syncPairId = Guid.NewGuid();
            var watcher = new FileSystemLocalSyncRootWatcher(syncPairId, _root);
            var observed = new TaskCompletionSource<LocalSyncRootChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, change) => observed.TrySetResult(change);

            await watcher.StartAsync();
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllText(Path.Combine(temporaryDirectory, "download.download"), "partial");

            await Task.Delay(TimeSpan.FromMilliseconds(350));
            await watcher.DisposeAsync();

            Assert.That(observed.Task.IsCompleted, Is.False);
        }

        [Test]
        public async Task StartAsync_PublishesRenameOldAndNewPaths()
        {
            Guid syncPairId = Guid.NewGuid();
            string oldPath = Path.Combine(_root, "old-name.txt");
            string newPath = Path.Combine(_root, "new-name.txt");
            File.WriteAllText(oldPath, "content");
            var watcher = new FileSystemLocalSyncRootWatcher(syncPairId, _root);
            var observed = new TaskCompletionSource<LocalSyncRootChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, change) =>
            {
                if (change.Kind == LocalSyncRootChangeKind.Renamed)
                {
                    observed.TrySetResult(change);
                }
            };

            await watcher.StartAsync();
            File.Move(oldPath, newPath);

            LocalSyncRootChange localChange = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watcher.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(localChange.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(localChange.OldFullPath, Is.EqualTo(oldPath));
                Assert.That(localChange.FullPath, Is.EqualTo(newPath));
                Assert.That(localChange.Kind, Is.EqualTo(LocalSyncRootChangeKind.Renamed));
            });
        }

        [Test]
        public async Task StartAsync_ExcelAtomicSaveDoesNotPublishParentOrTemporaryPaths()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Windows FileSystemWatcher and File.Replace semantics are required for this test.");
            }

            Guid syncPairId = Guid.NewGuid();
            string directoryPath = Path.Combine(_root, "Excel");
            Directory.CreateDirectory(directoryPath);
            string targetPath = Path.Combine(directoryPath, "Budget.xlsx");
            string temporaryPath = targetPath + ".111111.tmp";
            string lockPath = Path.Combine(directoryPath, "~$Budget.xlsx");
            File.WriteAllText(targetPath, "initial");
            var watcher = new FileSystemLocalSyncRootWatcher(syncPairId, _root);
            List<LocalSyncRootChange> observed = [];
            var targetObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, change) =>
            {
                lock (observed)
                {
                    observed.Add(change);
                }

                if (string.Equals(change.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetObserved.TrySetResult();
                }
            };

            await watcher.StartAsync();
            try
            {
                File.WriteAllText(lockPath, "lock");
                File.WriteAllText(temporaryPath, "updated");
                File.Replace(temporaryPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                await targetObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            finally
            {
                await watcher.DisposeAsync();
                File.Delete(temporaryPath);
                File.Delete(lockPath);
            }

            LocalSyncRootChange[] snapshot;
            lock (observed)
            {
                snapshot = observed.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    snapshot,
                    Has.Some.Matches<LocalSyncRootChange>(change =>
                        string.Equals(change.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)));
                Assert.That(
                    snapshot,
                    Has.None.Matches<LocalSyncRootChange>(change =>
                        string.Equals(change.FullPath, directoryPath, StringComparison.OrdinalIgnoreCase)));
                Assert.That(
                    snapshot,
                    Has.None.Matches<LocalSyncRootChange>(change =>
                        string.Equals(change.FullPath, temporaryPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(change.FullPath, lockPath, StringComparison.OrdinalIgnoreCase)));
            });
        }

        [Test]
        public async Task StartAsync_PublishesCrossDirectoryMoveAsDeleteAndCreate()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Windows FileSystemWatcher move semantics are required for this test.");
            }

            Guid syncPairId = Guid.NewGuid();
            string sourceDirectory = Path.Combine(_root, "source");
            string targetDirectory = Path.Combine(_root, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);
            string oldPath = Path.Combine(sourceDirectory, "old-name.txt");
            string newPath = Path.Combine(targetDirectory, "new-name.txt");
            File.WriteAllText(oldPath, "content");
            var watcher = new FileSystemLocalSyncRootWatcher(syncPairId, _root);
            List<LocalSyncRootChange> observed = [];
            var targetObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, change) =>
            {
                lock (observed)
                {
                    observed.Add(change);
                }

                if (string.Equals(change.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetObserved.TrySetResult();
                }
            };

            await watcher.StartAsync();
            File.Move(oldPath, newPath);

            await targetObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await watcher.DisposeAsync();

            LocalSyncRootChange[] snapshot;
            lock (observed)
            {
                snapshot = observed.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    snapshot,
                    Has.Some.Matches<LocalSyncRootChange>(change =>
                        change.Kind == LocalSyncRootChangeKind.Deleted
                        && string.Equals(change.FullPath, oldPath, StringComparison.OrdinalIgnoreCase)));
                Assert.That(
                    snapshot,
                    Has.Some.Matches<LocalSyncRootChange>(change =>
                        change.Kind == LocalSyncRootChangeKind.Created
                        && string.Equals(change.FullPath, newPath, StringComparison.OrdinalIgnoreCase)));
            });
        }

        [Test]
        public void Publish_ReportsErrorWhenFilterThrows()
        {
            Guid syncPairId = Guid.NewGuid();
            FileSystemLocalSyncRootWatcher watcher = new(syncPairId, _root);
            List<LocalSyncRootChange> observed = [];
            watcher.Changed += (_, change) => observed.Add(change);

            Assert.DoesNotThrow(() => watcher.Publish(string.Empty, LocalSyncRootChangeKind.Changed));

            Assert.Multiple(() =>
            {
                Assert.That(observed, Has.Count.EqualTo(1));
                Assert.That(observed[0].SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(observed[0].FullPath, Is.EqualTo(_root));
                Assert.That(observed[0].Kind, Is.EqualTo(LocalSyncRootChangeKind.Error));
            });
        }

        [Test]
        public void Publish_ContinuesAfterSubscriberException()
        {
            Guid syncPairId = Guid.NewGuid();
            string changedPath = Path.Combine(_root, "file.txt");
            FileSystemLocalSyncRootWatcher watcher = new(syncPairId, _root);
            List<LocalSyncRootChange> observed = [];
            watcher.Changed += (_, _) => throw new InvalidOperationException("Subscriber failed.");
            watcher.Changed += (_, change) => observed.Add(change);

            Assert.DoesNotThrow(() => watcher.Publish(changedPath, LocalSyncRootChangeKind.Changed));

            Assert.Multiple(() =>
            {
                Assert.That(observed, Has.Count.EqualTo(1));
                Assert.That(observed[0].SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(observed[0].FullPath, Is.EqualTo(changedPath));
                Assert.That(observed[0].Kind, Is.EqualTo(LocalSyncRootChangeKind.Changed));
            });
        }

        [Test]
        public async Task HandleError_PublishesErrorAndRestartsWatcher()
        {
            Guid syncPairId = Guid.NewGuid();
            string changedPath = Path.Combine(_root, "after-error.txt");
            FileSystemLocalSyncRootWatcher watcher = new(syncPairId, _root);
            TaskCompletionSource<LocalSyncRootChange> observedFileChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<LocalSyncRootChange> observed = [];
            watcher.Changed += (_, change) =>
            {
                observed.Add(change);
                if (string.Equals(change.FullPath, changedPath, StringComparison.OrdinalIgnoreCase)
                    && change.Kind is LocalSyncRootChangeKind.Created or LocalSyncRootChangeKind.Changed)
                {
                    observedFileChange.TrySetResult(change);
                }
            };
            await watcher.StartAsync();

            watcher.HandleError(new IOException("Watcher buffer overflow."));
            File.WriteAllText(changedPath, "content");

            LocalSyncRootChange fileChange = await observedFileChange.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watcher.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed.Any(change => change.Kind == LocalSyncRootChangeKind.Error), Is.True);
                Assert.That(fileChange.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(fileChange.FullPath, Is.EqualTo(changedPath));
            });
        }
    }
}
