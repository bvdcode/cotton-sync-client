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
        [TestCase("local", TestName = "SyncOnce_UploadsLocalFileAndPersistsBaseline")]
        [TestCase("local path with spaces", TestName = "SyncOnce_UploadsLocalFileFromRootPathWithSpacesAndPersistsBaseline")]
        [TestCase("локальный sync root", TestName = "SyncOnce_UploadsLocalFileFromUnicodeRootPathAndPersistsBaseline")]
        public async Task SyncOnce_UploadsLocalFileAndPersistsBaseline(string localRootName)
        {
            string localRoot = Path.Combine(_tempDirectory, localRootName);
            Directory.CreateDirectory(localRoot);
            const string relativePath = "hello.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from sync cli");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            DateTime lastWriteUtc = new(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(localFilePath, lastWriteUtc);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(remoteRootId, relativePath, contentHash, content);
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
                Assert.That(text, Does.Contain("Cotton Sync one-shot run"));
                Assert.That(text, Does.Contain("Uploaded hello.txt"));
                Assert.That(text, Does.Contain("State entries: 1"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.Kind, Is.EqualTo(SyncEntryKind.File));
                Assert.That(entry.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(contentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(handler.CreatedFileId));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/settings",
                    "/api/v1/chunks/" + contentHash + "/exists",
                    "/api/v1/chunks/raw?hash=" + contentHash,
                    "/api/v1/files/from-chunks",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }

        [Test]
        public async Task SyncOnce_WithRemotePathResolvesRootAndUploadsLocalFile()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-remote-path");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "remote-path.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from remote path sync cli");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-state-remote-path.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(remoteRootId, relativePath, contentHash, content);
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
                    "--remote-path",
                    "/",
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
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(output.ToString(), Does.Contain("Uploaded remote-path.txt"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(handler.CreatedFileId));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/resolver",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/settings",
                    "/api/v1/chunks/" + contentHash + "/exists",
                    "/api/v1/chunks/raw?hash=" + contentHash,
                    "/api/v1/files/from-chunks",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }

        [Test]
        public async Task SyncOnce_RefreshesTokenAndRetriesWhenAuthorizedSyncRequestReturnsUnauthorized()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-refresh");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "refresh.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello after refresh");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-state-refresh.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(
                remoteRootId,
                relativePath,
                contentHash,
                content,
                expireAccessTokenBeforeChunkExists: true);
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
            string chunkExistsPath = "/api/v1/chunks/" + contentHash + "/exists";
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(output.ToString(), Does.Contain("Uploaded refresh.txt"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/settings",
                    chunkExistsPath,
                    "/api/v1/auth/refresh?refreshToken=refresh-token",
                    chunkExistsPath,
                    "/api/v1/chunks/raw?hash=" + contentHash,
                    "/api/v1/files/from-chunks",
                    "/api/v1/auth/logout?refreshToken=refreshed-refresh-token",
                }));
                Assert.That(
                    handler.Requests
                        .Where(request => request.PathAndQuery == chunkExistsPath)
                        .Select(static request => request.AuthorizationParameter),
                    Is.EqualTo(new[] { "access-token", "refreshed-access-token" }));
            });
        }

        [Test]
        public async Task SyncOnce_WithBrowserLoginUploadsLocalFileAndPersistsBaseline()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-browser");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "hello-browser.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from browser sync cli");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-state-browser.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(
                remoteRootId,
                relativePath,
                contentHash,
                content,
                allowAppCodeAuth: true);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "cotton.test",
                    "--browser-login",
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
                Assert.That(text, Does.Contain("Approval URL: https://cotton.test/oauth/app-code/0190a000-0000-7000-8000-000000000022"));
                Assert.That(text, Does.Contain("Open this URL in your browser to approve sign-in."));
                Assert.That(text, Does.Contain("Waiting for browser approval..."));
                Assert.That(text, Does.Contain("Browser approval completed. Starting sync..."));
                Assert.That(text, Does.Contain("Cotton Sync one-shot run"));
                Assert.That(text, Does.Contain("Uploaded hello-browser.txt"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/poll",
                    "/api/v1/auth/me",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/settings",
                    "/api/v1/chunks/" + contentHash + "/exists",
                    "/api/v1/chunks/raw?hash=" + contentHash,
                    "/api/v1/files/from-chunks",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }

        [Test]
        public async Task SyncOnce_WithBrowserLoginRetriesNetworkUnavailableStartAndUploadsLocalFile()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-browser-network-retry");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "hello-browser-retry.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello after browser network retry");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-state-browser-network-retry.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(
                remoteRootId,
                relativePath,
                contentHash,
                content,
                allowAppCodeAuth: true,
                appCodeStartNetworkFailuresBeforeSuccess: 3);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "cotton.test",
                    "--browser-login",
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
                Assert.That(text, Does.Contain("Transient sync failure: Browser sign-in could not contact the server. Check network or firewall access and retry."));
                Assert.That(text, Does.Contain("Retrying attempt 2 of 3 after 1s."));
                Assert.That(text, Does.Contain("Approval URL: https://cotton.test/oauth/app-code/0190a000-0000-7000-8000-000000000022"));
                Assert.That(text, Does.Contain("Browser approval completed. Starting sync..."));
                Assert.That(text, Does.Contain("Uploaded hello-browser-retry.txt"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/poll",
                    "/api/v1/auth/me",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111",
                    "/api/v1/layouts/nodes/11111111-1111-1111-1111-111111111111/children?page=1&pageSize=500&depth=0",
                    "/api/v1/settings",
                    "/api/v1/chunks/" + contentHash + "/exists",
                    "/api/v1/chunks/raw?hash=" + contentHash,
                    "/api/v1/files/from-chunks",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }
    }
}
