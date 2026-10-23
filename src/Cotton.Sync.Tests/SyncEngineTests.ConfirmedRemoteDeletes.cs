// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Models.Enums;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_ConfirmedFolderDeleteRemovesLargePlaceholderSubtreeAndReceivesOtherFiles(bool scoped)
        {
            const string folderPath = "Archive";
            RemoteDirectorySnapshot folder = RemoteDirectory(folderPath);
            RemoteDirectorySnapshot child = RemoteDirectory("Archive/Nested", folder.Node.Id);
            LocalFileSnapshot[] placeholders = Enumerable.Range(0, 101)
                .Select(index => CloudFilesPlaceholderLocal($"Archive/Nested/{index}.bin", 8)).ToArray();
            FakeLocalFileScanner scanner = new(placeholders);
            scanner.Directories.AddRange([LocalDirectory(folderPath), LocalDirectory(child.RelativePath)]);
            SqliteSyncStateStore store = new(_databasePath);
            PlaceholderLocalWriter writer = new(placeholders);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            NodeFileManifestDto incoming = RemoteFile("New-model.stl", HashText("model"), sizeBytes: 5);
            SyncEngine engine = new(scanner, new FakeRemoteTreeCrawler(RemoteTree(incoming)), remoteFiles, store,
                localWriter: writer, remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(store, folderPath, folder.Node);
            await InsertDirectoryBaselineAsync(store, child.RelativePath, child.Node);
            foreach (LocalFileSnapshot placeholder in placeholders)
            {
                WriteFile(placeholder.RelativePath, string.Empty);
                NodeFileManifestDto remote = RemoteFile(placeholder.RelativePath, HashText("baseline"), sizeBytes: 8);
                remote.NodeId = child.Node.Id;
                await InsertPlaceholderBaselineAsync(store, placeholder.RelativePath, remote);
            }

            SyncRunOptions options = new()
            {
                RemoteChanges = RemoteChangeFeedSnapshot.FromChanges(
                    [RemoteDeleteChange(1, SyncChangeKind.FolderDeleted, folder.Node.Id, folderPath)]),
            };
            if (scoped)
            {
                options.Scope = SyncRunScope.ForLocalChangedPaths(
                    [folderPath, child.RelativePath, "New-model.stl", .. placeholders.Select(file => file.RelativePath)]);
            }

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);
            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairEntriesAsync("pair-a").ToListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False, result.ActionRequiredMessage);
                Assert.That(result.HasDeferredLocalPaths, Is.False);
                Assert.That(writer.DeleteCalls, Is.EqualTo(101));
                Assert.That(Directory.Exists(Path.Combine(_root, folderPath)), Is.False);
                Assert.That(remaining.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "New-model.stl" }));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { "New-model.stl" }));
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_UnconfirmedRemoteAbsenceCountsOnlineOnlyFilesInDeleteGuard()
        {
            LocalFileSnapshot[] placeholders = Enumerable.Range(0, 101)
                .Select(index => CloudFilesPlaceholderLocal($"file-{index}.bin", 8)).ToArray();
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(placeholders), EmptyRemoteTree(), remoteFiles,
                out SqliteSyncStateStore store);
            foreach (LocalFileSnapshot placeholder in placeholders)
            {
                WriteFile(placeholder.RelativePath, string.Empty);
                await InsertPlaceholderBaselineAsync(store, placeholder.RelativePath,
                    RemoteFile(placeholder.RelativePath, HashText("baseline"), sizeBytes: 8));
            }

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { AllowInitialVirtualFilesStreaming = false });
            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairEntriesAsync("pair-a").ToListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.ActionRequiredMessage, Does.Contain("101 pending deletes exceed limit 100"));
                Assert.That(remaining, Has.Count.EqualTo(101));
                Assert.That(placeholders.All(file => File.Exists(file.FullPath)), Is.True);
                Assert.That(result.Activities.All(activity => activity.Kind == SyncActivityKind.Skipped), Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_ConfirmedDeletesPreserveLocalEditsAndDoNotAuthorizeOtherMissingFiles()
        {
            string[] paths = ["confirmed-a.txt", "confirmed-b.txt", "modified.txt", "unconfirmed-a.txt", "unconfirmed-b.txt"];
            List<LocalFileSnapshot> localFiles = [];
            List<SyncChangeDto> changes = [];
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore store = new(_databasePath);
            for (int index = 0; index < paths.Length; index++)
            {
                string path = paths[index];
                string content = path == "modified.txt" ? "local edit" : "baseline";
                WriteFile(path, content);
                localFiles.Add(LocalFile(path, content));
                NodeFileManifestDto remote = RemoteFile(path, HashText("baseline"), sizeBytes: 8);
                await InsertBaselineAsync(store, path, HashText("baseline"), remote);
                if (index < 3)
                {
                    changes.Add(RemoteDeleteChange(index + 1, SyncChangeKind.FileDeleted, remote.Id, path));
                }
            }
            SyncEngine engine = new(new FakeLocalFileScanner([.. localFiles]),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, store);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions
            {
                MaximumLocalDeletesPerRun = 1,
                RemoteChanges = RemoteChangeFeedSnapshot.FromChanges(changes),
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.ActionRequiredMessage, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(File.Exists(Path.Combine(_root, paths[0])), Is.False);
                Assert.That(File.Exists(Path.Combine(_root, paths[1])), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(_root, paths[2])), Is.EqualTo("local edit"));
                Assert.That(File.Exists(Path.Combine(_root, paths[3])), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, paths[4])), Is.True);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { "modified.txt" }));
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });
        }

        private SyncChangeDto RemoteDeleteChange(long cursor, SyncChangeKind kind, Guid itemId, string name)
        {
            return new SyncChangeDto
            {
                Id = cursor,
                Kind = kind,
                ItemId = itemId,
                ParentNodeId = _remoteRootNodeId,
                LayoutId = Guid.NewGuid(),
                Name = name,
                CreatedAt = DateTime.UtcNow,
            };
        }
    }
}
