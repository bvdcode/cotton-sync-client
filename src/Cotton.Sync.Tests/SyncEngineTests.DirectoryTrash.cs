// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [Test]
        public async Task RunOnceAsync_DeletedFolderDoesNotDiscardDivergedBaseline()
        {
            RemoteDirectorySnapshot root = RemoteDirectory("Pictures");
            NodeFileManifestDto file = RemoteFile("Pictures/photo.jpg", HashText("remote photo"), sizeBytes: 1024);
            RemoteTreeSnapshot tree = RemoteTree(file);
            tree.Directories.Add(root);
            SqliteSyncStateStore store = new(_databasePath);
            await InsertDirectoryBaselineAsync(store, root.RelativePath, root.Node);
            await InsertBaselineAsync(store, "Pictures/photo.jpg", HashText("unsynchronized local photo"), file);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SyncEngine engine = new(new FakeLocalFileScanner(), new DescendantPathRemoteTreeCrawler(tree), remoteFiles, store,
                remoteDirectories: remoteDirectories);

            await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths(["Pictures"], ["Pictures"]) });

            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });
        }

        [TestCase(SyncPlaceholderHydrationState.RemoteOnly)]
        [TestCase(SyncPlaceholderHydrationState.Hydrated)]
        public async Task RunOnceAsync_DeletedFolderWith238FilesUsesOneTrashOperation(SyncPlaceholderHydrationState hydrationState)
        {
            RemoteDirectorySnapshot root = RemoteDirectory("Pictures");
            NodeFileManifestDto[] files = Enumerable.Range(0, 238)
                .Select(index => RemoteFile($"Pictures/{index}.jpg", HashText(index.ToString()), sizeBytes: 1024))
                .ToArray();
            RemoteTreeSnapshot tree = RemoteTree(files);
            tree.Directories.Add(root);
            DescendantPathRemoteTreeCrawler crawler = new(tree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore store = new(_databasePath);
            await InsertDirectoryBaselineAsync(store, root.RelativePath, root.Node);
            foreach (NodeFileManifestDto file in files)
            {
                await InsertPlaceholderBaselineAsync(store, "Pictures/" + file.Name, file, hydrationState);
            }
            SyncEngine engine = new(new FakeLocalFileScanner(), crawler, remoteFiles, store,
                remoteDirectories: remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths(["Pictures"], ["Pictures"]),
                    MaximumRemoteDeletesPerRun = 1,
                });

            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities, Has.Count.EqualTo(1));
                Assert.That(result.Activities[0].RelativePath, Is.EqualTo("Pictures"));
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Downloads, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (root.Node.Id, false) }));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remaining, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_DeletedFolderRetainsBaselineUntilTrashAndStateWritesSucceed(bool failStateWrite)
        {
            RemoteDirectorySnapshot root = RemoteDirectory("Pictures");
            NodeFileManifestDto file = RemoteFile("Pictures/photo.jpg", HashText("photo"), sizeBytes: 1024);
            RemoteTreeSnapshot tree = RemoteTree(file);
            tree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore store = new(_databasePath);
            await InsertDirectoryBaselineAsync(store, root.RelativePath, root.Node);
            await InsertPlaceholderBaselineAsync(store, "Pictures/photo.jpg", file);
            ISyncStateStore runStore = store;
            if (failStateWrite)
            {
                runStore = new FailingDeleteStateStore(store);
                remoteDirectories.OnDeleted = () =>
                {
                    tree.Files.Clear();
                    tree.Directories.Clear();
                };
            }
            else
            {
                remoteDirectories.DeleteFailure = new InvalidOperationException("Remote delete failed.");
            }
            DescendantPathRemoteTreeCrawler crawler = new(tree);
            SyncEngine engine = new(new FakeLocalFileScanner(), crawler, remoteFiles, runStore,
                remoteDirectories: remoteDirectories);
            SyncRunOptions options = new() { Scope = SyncRunScope.ForLocalChangedPaths(["Pictures"], ["Pictures"]) };
            SyncPair pair = Pair(SyncPairMaterializationMode.WindowsVirtualFiles);

            Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunOnceAsync(pair, options));
            IReadOnlyList<SyncStateEntry> pending = await store.LoadPairAsync("pair-a");
            Assert.That(pending, Has.Count.EqualTo(2));
            remoteDirectories.DeleteFailure = null;
            SyncEngine retry = new(new FakeLocalFileScanner(), crawler, remoteFiles, new SqliteSyncStateStore(_databasePath),
                remoteDirectories: remoteDirectories);

            SyncRunResult result = await retry.RunOnceAsync(pair, options);

            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (root.Node.Id, false) }));
                Assert.That(remaining, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_DeletedFolderIsPreservedIfLocalPathReappears()
        {
            RemoteDirectorySnapshot root = RemoteDirectory("Pictures");
            NodeFileManifestDto file = RemoteFile("Pictures/photo.jpg", HashText("photo"), sizeBytes: 1024);
            RemoteTreeSnapshot tree = RemoteTree(file);
            tree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore store = new(_databasePath);
            await InsertDirectoryBaselineAsync(store, root.RelativePath, root.Node);
            await InsertPlaceholderBaselineAsync(store, "Pictures/photo.jpg", file);
            WriteFile("Pictures/new.txt", "new local file");
            SyncEngine engine = new(new FakeLocalFileScanner(), new DescendantPathRemoteTreeCrawler(tree), remoteFiles, store,
                remoteDirectories: remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths(["Pictures"], ["Pictures"]) });

            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remaining, Has.Count.EqualTo(2));
                Assert.That(result.RequiresUserAction, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_DeletedFolderApprovalIsBoundToItsContents(bool replaceChild)
        {
            RemoteDirectorySnapshot root = RemoteDirectory("Pictures");
            NodeFileManifestDto file = RemoteFile("Pictures/photo.jpg", HashText("photo"), sizeBytes: 1024);
            RemoteTreeSnapshot tree = RemoteTree(file);
            tree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore store = new(_databasePath);
            await InsertDirectoryBaselineAsync(store, root.RelativePath, root.Node);
            await InsertPlaceholderBaselineAsync(store, "Pictures/photo.jpg", file);
            SyncEngine engine = new(new FakeLocalFileScanner(), new DescendantPathRemoteTreeCrawler(tree), remoteFiles, store,
                remoteDirectories: remoteDirectories);
            SyncPair pair = Pair(SyncPairMaterializationMode.WindowsVirtualFiles);
            SyncRunOptions options = new()
            {
                Scope = SyncRunScope.ForLocalChangedPaths(["Pictures"], ["Pictures"]),
                MaximumRemoteDeletesPerRun = 0,
            };
            SyncRunResult blocked = await engine.RunOnceAsync(pair, options);
            Assert.That(blocked.RequiresUserAction, Is.True);
            const string marker = "Plan fingerprint ";
            string details = blocked.Activities.Single().Details!;
            string fingerprint = details.Substring(details.IndexOf(marker, StringComparison.Ordinal) + marker.Length, 64);
            options.ApprovedRemoteDeletePlan = new RemoteDeletePlanApproval(1, fingerprint);
            if (replaceChild)
            {
                NodeFileManifestDto replacement = RemoteFile("Pictures/photo.jpg", HashText("different photo"), sizeBytes: 2048);
                tree.Files.Clear();
                tree.Files.AddRange(RemoteTree(replacement).Files);
                await InsertPlaceholderBaselineAsync(store, "Pictures/photo.jpg", replacement);
            }

            SyncRunResult result = await engine.RunOnceAsync(pair, options);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.EqualTo(replaceChild));
                Assert.That(remoteDirectories.Deletes.Count, Is.EqualTo(replaceChild ? 0 : 1));
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });
        }
    }
}
