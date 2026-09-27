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
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_AdoptsIdenticalExportedFolderAfterRemoteNameChange(bool scoped)
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

            IReadOnlyList<SyncStateEntry> states = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, sourcePath)), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(_root, targetFilePath)), Is.EqualTo(content));
                Assert.That(Directory.EnumerateFiles(Path.Combine(_root, ".cotton-sync", "deleted"),
                    "song.bin", SearchOption.AllDirectories).Any(), Is.True);
                Assert.That(states.Single(state => state.RemoteNodeId == originalDirectory.Node.Id).RelativePath,
                    Is.EqualTo(targetPath));
                Assert.That(states.Single(state => state.RemoteFileId == remoteFile.Id).RelativePath,
                    Is.EqualTo(targetFilePath));
                Assert.That(remoteDirectories.Creates, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
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
            remoteTree.Directories.AddRange([remoteDirectory, remoteChild]);
            RemoteDirectoryDuplicateCoalescer coalescer = new(
                scanner,
                new FakeRemoteTreeCrawler(remoteTree),
                new AtomicLocalFileSyncWriter(),
                stateStore,
                new SyncLocalContentHashResolver(scanner, null),
                NullLogger.Instance);

            await coalescer.CoalesceAsync(context);

            IReadOnlyList<SyncStateEntry> actualStates = await stateStore.LoadPairAsync("pair-a");
            bool expectedAdoption = !differentContent && !extraFile && !extraSourceFile;
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, sourcePath)), Is.EqualTo(!expectedAdoption));
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
