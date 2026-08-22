// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Sync;
using Cotton.Sync.Cli;
using Cotton.Sync.Cli.Tests.TestSupport;
using Cotton.Sync.State;

namespace Cotton.Sync.Cli.Tests
{
    public partial class SyncCliCommandRunnerTests
    {
        [Test]
        public async Task SyncOnce_UploadsEmptyLocalDirectoryAndPersistsDirectoryBaseline()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-empty-directory");
            const string relativePath = "Projects";
            Directory.CreateDirectory(Path.Combine(localRoot, relativePath));
            string databasePath = Path.Combine(_tempDirectory, "sync-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceDirectoryServerHandler handler = new SyncOnceDirectoryServerHandler(remoteRootId, relativePath);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "cotton.test",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    localRoot,
                    "--remote-root",
                    remoteRootId.ToString("D"),
                    "--sync-pair",
                    syncPairId,
                    "--database",
                    databasePath,
                ],
                output,
                error,
                httpClient);

            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            SyncStateEntry? entry = await store.GetAsync(syncPairId, relativePath);
            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Uploaded Projects - Created remote folder."));
                Assert.That(text, Does.Contain("State entries: 1"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.Kind, Is.EqualTo(SyncEntryKind.Directory));
                Assert.That(entry.RemoteNodeId, Is.EqualTo(handler.CreatedDirectoryId));
                Assert.That(entry.LocalContentHash, Is.Null);
                Assert.That(entry.RemoteContentHash, Is.Null);
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/layouts/nodes",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }
    }
}
