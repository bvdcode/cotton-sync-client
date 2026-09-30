// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [Test]
        public async Task RunOnceAsync_DoesNotAdoptEquivalentCloudFolderWithoutBaseline()
        {
            const string cloudPath = "Music/Michael Brun";
            const string localPath = "Music/Michaël Brun";
            Directory.CreateDirectory(Path.Combine(_root, localPath));
            RemoteDirectorySnapshot parent = RemoteDirectory("Music");
            RemoteDirectorySnapshot cloud = RemoteDirectory(cloudPath, parent.Node.Id);
            RemoteTreeSnapshot remoteTree = RemoteTree();
            remoteTree.Directories.AddRange([parent, cloud]);
            FakeLocalFileScanner scanner = new()
            {
                Directories = { LocalDirectory("Music"), LocalDirectory(localPath) },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);

            InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));
            });

            Assert.Multiple(() =>
            {
                Assert.That(error?.Message, Does.Contain("no local sync baseline"));
                Assert.That(Directory.Exists(Path.Combine(_root, localPath)), Is.True);
                Assert.That(remoteDirectories.CreateAttempts, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
            });
            Assert.That(await stateStore.LoadPairAsync("pair-a"), Is.Empty);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task RunOnceAsync_KeepsServerEquivalentLocalFolders(bool scoped, bool differentContent)
        {
            const string parentPath = "Music";
            const string sourcePath = "Music/Michael Brun";
            const string sourceChildPath = "Music/Michael Brun/Album";
            const string sourceFilePath = "Music/Michael Brun/Album/song.bin";
            const string duplicatePath = "Music/Michaël Brun";
            const string duplicateChildPath = "Music/Michaël Brun/Album";
            const string duplicateFilePath = "Music/Michaël Brun/Album/song.bin";
            const string content = "matching music";
            WriteFile(sourceFilePath, content);
            string duplicateContent = differentContent ? "different music" : content;
            WriteFile(duplicateFilePath, duplicateContent);
            LocalFileSnapshot sourceFile = LocalFile(sourceFilePath, content);
            LocalFileSnapshot duplicateFile = LocalFile(duplicateFilePath, duplicateContent);
            RemoteDirectorySnapshot parent = RemoteDirectory(parentPath);
            RemoteDirectorySnapshot source = RemoteDirectory(sourcePath, parent.Node.Id);
            RemoteDirectorySnapshot child = RemoteDirectory(sourceChildPath, source.Node.Id);
            NodeFileManifestDto remoteFile = RemoteFile(sourceFilePath, sourceFile.ContentHash, sizeBytes: sourceFile.SizeBytes);
            remoteFile.NodeId = child.Node.Id;
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.AddRange([parent, source, child]);
            FakeLocalFileScanner scanner = new(sourceFile, duplicateFile)
            {
                Directories =
                {
                    LocalDirectory(parentPath),
                    LocalDirectory(sourcePath),
                    LocalDirectory(sourceChildPath),
                    LocalDirectory(duplicatePath),
                    LocalDirectory(duplicateChildPath),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            remoteDirectories.ExistingDirectories.Add(source.Node);
            remoteDirectories.ConflictCreates.Add((parent.Node.Id, "Michaël Brun"));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                new FakeRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, parent.Node);
            await InsertDirectoryBaselineAsync(stateStore, sourcePath, source.Node);
            await InsertDirectoryBaselineAsync(stateStore, sourceChildPath, child.Node);
            await InsertBaselineAsync(stateStore, sourceFilePath, sourceFile.ContentHash, remoteFile, sourceFile.SizeBytes);

            SyncRunOptions options = scoped
                ? new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([duplicatePath]) }
                : new SyncRunOptions();
            SyncRunResult? result = null;
            InvalidOperationException? error = null;
            if (differentContent)
            {
                error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);
                });
            }
            else
            {
                result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);
                await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);
            }

            IReadOnlyList<SyncStateEntry> states = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, sourceFilePath)), Is.EqualTo(content));
                Assert.That(Directory.Exists(Path.Combine(_root, duplicatePath)), Is.True);
                Assert.That(File.ReadAllText(Path.Combine(_root, duplicateFilePath)), Is.EqualTo(duplicateContent));
                Assert.That(Directory.Exists(Path.Combine(_root, ".cotton-sync", "deleted")), Is.False);
                if (differentContent)
                {
                    Assert.That(error?.Message, Does.Contain("Both folders were kept"));
                }
                else
                {
                    Assert.That(result?.RequiresUserAction, Is.False);
                }
                Assert.That(states.Select(state => state.RelativePath),
                    Is.EqualTo(new[] { parentPath, sourcePath, sourceChildPath, sourceFilePath }));
                Assert.That(states.Single(state => state.Kind == SyncEntryKind.File).RemoteFileId,
                    Is.EqualTo(remoteFile.Id));
                Assert.That(remoteDirectories.CreateAttempts, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_KeepsIdenticalLocalFoldersAfterRemoteNameChange(bool scoped)
        {
            const string sourcePath = "Music/Cafe";
            const string sourceChildPath = "Music/Cafe/Album";
            const string sourceFilePath = "Music/Cafe/Album/song.bin";
            const string targetPath = "Music/Café";
            const string targetChildPath = "Music/Café/Album";
            const string targetFilePath = "Music/Café/Album/song.bin";
            const string content = "same music content";
            WriteFile(sourceFilePath, content);
            WriteFile(targetFilePath, content);
            LocalFileSnapshot sourceFile = LocalFile(sourceFilePath, content);
            LocalFileSnapshot targetFile = LocalFile(targetFilePath, content);
            RemoteDirectorySnapshot originalDirectory = RemoteDirectory(sourcePath);
            RemoteDirectorySnapshot originalChild = RemoteDirectory(sourceChildPath, originalDirectory.Node.Id);
            RemoteDirectorySnapshot remoteDirectory = new()
            {
                RelativePath = targetPath,
                Node = new NodeDto
                {
                    Id = originalDirectory.Node.Id,
                    ParentId = originalDirectory.Node.ParentId,
                    Name = "Café",
                },
            };
            RemoteDirectorySnapshot remoteChild = new()
            {
                RelativePath = targetChildPath,
                Node = new NodeDto
                {
                    Id = originalChild.Node.Id,
                    ParentId = remoteDirectory.Node.Id,
                    Name = "Album",
                },
            };
            NodeFileManifestDto remoteFile = RemoteFile(targetFilePath, sourceFile.ContentHash, sizeBytes: sourceFile.SizeBytes);
            remoteFile.NodeId = remoteChild.Node.Id;
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.AddRange([remoteDirectory, remoteChild]);
            FakeLocalFileScanner scanner = new(sourceFile, targetFile)
            {
                Directories =
                {
                    LocalDirectory(sourcePath),
                    LocalDirectory(sourceChildPath),
                    LocalDirectory(targetPath),
                    LocalDirectory(targetChildPath),
                },
            };
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, sourcePath, originalDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, sourceChildPath, originalChild.Node);
            await InsertBaselineAsync(stateStore, sourceFilePath, sourceFile.ContentHash, remoteFile, sourceFile.SizeBytes);

            SyncRunOptions options = scoped
                ? new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([sourcePath, targetPath]) }
                : new SyncRunOptions();
            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);
            await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles), options);

            IReadOnlyList<SyncStateEntry> states = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, sourceFilePath)), Is.EqualTo(content));
                Assert.That(File.ReadAllText(Path.Combine(_root, targetFilePath)), Is.EqualTo(content));
                Assert.That(Directory.Exists(Path.Combine(_root, ".cotton-sync", "deleted")), Is.False);
                Assert.That(states.Single(state => state.RemoteNodeId == originalDirectory.Node.Id).RelativePath,
                    Is.EqualTo(targetPath));
                Assert.That(states.Single(state => state.RemoteFileId == remoteFile.Id).RelativePath,
                    Is.EqualTo(targetFilePath));
                Assert.That(remoteDirectories.Creates, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        public async Task DuplicateDirectoryCoalescer_OnlyAdoptsByteIdenticalTrees(
            bool differentContent,
            bool extraFile,
            bool extraSourceFile)
        {
            const string sourcePath = "Music/Cafe";
            const string sourceChildPath = "Music/Cafe/Album";
            const string sourceFilePath = "Music/Cafe/Album/song.bin";
            const string targetPath = "Music/Café";
            const string targetChildPath = "Music/Café/Album";
            const string targetFilePath = "Music/Café/Album/song.bin";
            const string content = "same music content";
            string exportedContent = differentContent ? "different music content" : content;
            WriteFile(sourceFilePath, content);
            WriteFile(targetFilePath, exportedContent);
            if (extraFile)
            {
                WriteFile("Music/Café/Album/extra.bin", "untracked data");
            }
            if (extraSourceFile)
            {
                WriteFile("Music/Cafe/Album/extra.bin", "untracked source data");
            }

            LocalFileSnapshot sourceFile = LocalFile(sourceFilePath, content);
            LocalFileSnapshot targetFile = LocalFile(targetFilePath, exportedContent);
            RemoteDirectorySnapshot originalDirectory = RemoteDirectory(sourcePath);
            RemoteDirectorySnapshot originalChild = RemoteDirectory(sourceChildPath, originalDirectory.Node.Id);
            RemoteDirectorySnapshot remoteDirectory = new()
            {
                RelativePath = targetPath,
                Node = new NodeDto
                {
                    Id = originalDirectory.Node.Id,
                    ParentId = originalDirectory.Node.ParentId,
                    Name = "Café",
                },
            };
            RemoteDirectorySnapshot remoteChild = new()
            {
                RelativePath = targetChildPath,
                Node = new NodeDto
                {
                    Id = originalChild.Node.Id,
                    ParentId = remoteDirectory.Node.Id,
                    Name = "Album",
                },
            };
            NodeFileManifestDto remoteFile = RemoteFile(targetFilePath, sourceFile.ContentHash, sizeBytes: sourceFile.SizeBytes);
            remoteFile.NodeId = remoteChild.Node.Id;
            FakeLocalFileScanner scanner = new(sourceFile, targetFile)
            {
                Directories =
                {
                    LocalDirectory(sourcePath),
                    LocalDirectory(sourceChildPath),
                    LocalDirectory(targetPath),
                    LocalDirectory(targetChildPath),
                },
            };
            if (extraFile)
            {
                scanner.Files.Add(LocalFile("Music/Café/Album/extra.bin", "untracked data"));
            }

            SqliteSyncStateStore stateStore = new(_databasePath);
            await stateStore.InitializeAsync();
            await InsertDirectoryBaselineAsync(stateStore, sourcePath, originalDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, sourceChildPath, originalChild.Node);
            await InsertBaselineAsync(stateStore, sourceFilePath, sourceFile.ContentHash, remoteFile, sourceFile.SizeBytes);
            SyncTreeLookups lookups = new(
                scanner.Directories.ToDictionary(directory => SyncPath.ToKey(directory.RelativePath), StringComparer.OrdinalIgnoreCase),
                new[] { remoteDirectory, remoteChild }.ToDictionary(directory => SyncPath.ToKey(directory.RelativePath), StringComparer.OrdinalIgnoreCase),
                scanner.Files.ToDictionary(file => SyncPath.ToKey(file.RelativePath), StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, RemoteFileSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    [SyncPath.ToKey(targetFilePath)] = new RemoteFileSnapshot
                    {
                        RelativePath = targetFilePath,
                        File = remoteFile,
                    },
                },
                new NodeDto { Id = _remoteRootNodeId });
            IReadOnlyList<SyncStateEntry> originalStates = await stateStore.LoadPairAsync("pair-a");
            SyncRunContext context = new(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions(),
                new SyncRunResult(),
                lookups,
                originalStates.Where(state => state.Kind == SyncEntryKind.Directory)
                    .ToDictionary(state => SyncPath.ToKey(state.RelativePath), StringComparer.OrdinalIgnoreCase),
                originalStates.Where(state => state.Kind == SyncEntryKind.File)
                    .ToDictionary(state => SyncPath.ToKey(state.RelativePath), StringComparer.OrdinalIgnoreCase),
                null,
                DateTime.UtcNow,
                CancellationToken.None);
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.AddRange([RemoteDirectory("Music"), remoteDirectory, remoteChild]);
            RemoteDirectoryDuplicateCoalescer coalescer = new(
                scanner,
                new FakeRemoteTreeCrawler(remoteTree),
                stateStore,
                new SyncLocalContentHashResolver(scanner, null),
                NullLogger.Instance,
                null);

            await coalescer.CoalesceAsync(context);

            IReadOnlyList<SyncStateEntry> actualStates = await stateStore.LoadPairAsync("pair-a");
            bool expectedAdoption = !differentContent && !extraFile && !extraSourceFile;
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, sourcePath)), Is.True);
                Assert.That(File.ReadAllText(Path.Combine(_root, targetFilePath)), Is.EqualTo(exportedContent));
                Assert.That(actualStates.Single(state => state.Kind == SyncEntryKind.File).RemoteFileId,
                    Is.EqualTo(remoteFile.Id));
                Assert.That(actualStates.Single(state => state.Kind == SyncEntryKind.File).RelativePath,
                    Is.EqualTo(expectedAdoption ? targetFilePath : sourceFilePath));
                Assert.That(actualStates.Single(state => state.Kind == SyncEntryKind.Directory
                    && state.RemoteNodeId == originalDirectory.Node.Id).RelativePath,
                    Is.EqualTo(expectedAdoption ? targetPath : sourcePath));
            });
        }
    }
}
