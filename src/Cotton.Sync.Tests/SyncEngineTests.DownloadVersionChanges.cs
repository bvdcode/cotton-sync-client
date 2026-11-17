// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Text;
using Cotton.Files;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [Test]
        public async Task RunOnceAsync_RemoteVersionChangesPreserveBaselineAndDownloadLatestOnRetry()
        {
            const string path = "changing.bin";
            WriteFile(path, "original");
            LocalFileSnapshot local = LocalFile(path, "original");
            NodeFileManifestDto original = RemoteFile(path, HashText("original"), sizeBytes: 8);
            NodeFileManifestDto listed = RemoteFile(path, HashText("listed"), original.Id, 6);
            byte[] latestBytes = Encoding.UTF8.GetBytes("latest contents");
            NodeFileManifestDto latest = RemoteFile(path, Hash(latestBytes), original.Id, latestBytes.Length);
            FakeRemoteFileSynchronizer remote = new()
            {
                BeforeDownload = _ => throw new CottonApiException(HttpStatusCode.PreconditionFailed, null, "Content changed."),
            };
            remote.Downloads[original.Id] = latestBytes;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), remote, out SqliteSyncStateStore store,
                RemoteTree(listed), RemoteTree(latest));
            await InsertBaselineAsync(store, path, local.ContentHash!, original);

            Assert.ThrowsAsync<RemoteFileVersionChangedException>(() => engine.RunOnceAsync(Pair()));
            SyncStateEntry? unchanged = await store.GetAsync("pair-a", path);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, path)), Is.EqualTo("original"));
                Assert.That(unchanged!.RemoteContentHash, Is.EqualTo(original.ContentHash));
                Assert.That(unchanged.LocalContentHash, Is.EqualTo(local.ContentHash));
            });

            remote.BeforeDownload = null;
            await engine.RunOnceAsync(Pair());
            SyncStateEntry? completed = await store.GetAsync("pair-a", path);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(Path.Combine(_root, path)), Is.EqualTo(latestBytes));
                Assert.That(completed!.RemoteContentHash, Is.EqualTo(latest.ContentHash));
                Assert.That(completed.LocalContentHash, Is.EqualTo(latest.ContentHash));
                Assert.That(remote.Uploads, Is.Empty);
            });
        }
    }
}
