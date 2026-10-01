// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(SyncPlaceholderHydrationState.RemoteOnly, false)]
        [TestCase(SyncPlaceholderHydrationState.Dehydrated, false)]
        [TestCase(SyncPlaceholderHydrationState.RemoteOnly, true)]
        public async Task RunOnceAsync_RemoteUpdateAfterPreviousVersionHydrationDoesNotCreateFalseConflict(
            SyncPlaceholderHydrationState hydrationState,
            bool userEdited)
        {
            const string path = "Docs/document.txt";
            const string previousContent = "previous remote content";
            const string newContent = "new remote";
            string localContent = previousContent;
            if (userEdited)
            {
                localContent = "a real local edit";
            }
            WriteFile(path, localContent);
            Guid id = Guid.NewGuid();
            NodeFileManifestDto previous = RemoteFile(path, HashText(previousContent), id,
                sizeBytes: Encoding.UTF8.GetByteCount(previousContent));
            NodeFileManifestDto current = RemoteFile(path, HashText(newContent), id,
                sizeBytes: Encoding.UTF8.GetByteCount(newContent));
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.Downloads[id] = Encoding.UTF8.GetBytes(newContent);
            SyncEngine engine = CreateEngine(new LocalFileScanner(), RemoteTree(current), remoteFiles,
                out SqliteSyncStateStore store);
            await InsertPlaceholderBaselineAsync(store, path, previous, hydrationState);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? state = await store.GetAsync("pair-a", path);
            string[] conflicts = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(state!.RemoteFileId, Is.EqualTo(id));
                Assert.That(state.RemoteContentHash, Is.EqualTo(current.ContentHash));
                Assert.That(remoteFiles.DownloadCalls, Is.EqualTo(new[] { id }));
            });
            if (userEdited)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(conflicts, Has.Length.EqualTo(1));
                    Assert.That(File.ReadAllText(Path.Combine(_root, "Docs", "document.txt")), Is.EqualTo(localContent));
                    Assert.That(File.ReadAllText(conflicts.Single()), Is.EqualTo(newContent));
                    Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Conflict), Is.True);
                });
                return;
            }
            Assert.Multiple(() =>
            {
                Assert.That(conflicts, Is.Empty);
                Assert.That(File.ReadAllText(Path.Combine(_root, "Docs", "document.txt")), Is.EqualTo(newContent));
                Assert.That(state!.LocalContentHash, Is.EqualTo(current.ContentHash));
                Assert.That(state.LocalSizeBytes, Is.EqualTo(current.SizeBytes));
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Conflict), Is.False);
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Downloaded), Is.True);
            });
        }
    }
}
