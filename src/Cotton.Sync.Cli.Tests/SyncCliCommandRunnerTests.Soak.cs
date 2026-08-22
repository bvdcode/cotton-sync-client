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
        public async Task SyncSoak_RunsOneIterationAndPrintsSummary()
        {
            string localRoot = Path.Combine(_tempDirectory, "soak-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "soak.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from sync soak");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            File.SetLastWriteTimeUtc(localFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-soak-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(remoteRootId, relativePath, contentHash, content);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                    "--iterations",
                    "1",
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
                Assert.That(text, Does.Contain("Cotton Sync soak run"));
                Assert.That(text, Does.Contain("Iteration 1: activities=1, deferredLocalPaths=0, stateEntries=1"));
                Assert.That(text, Does.Contain("elapsedSeconds="));
                Assert.That(text, Does.Contain("Elapsed seconds:"));
                Assert.That(text, Does.Contain("CPU seconds:"));
                Assert.That(text, Does.Contain("CPU utilization percent:"));
                Assert.That(text, Does.Contain("Start working set bytes:"));
                Assert.That(text, Does.Contain("End working set bytes:"));
                Assert.That(text, Does.Contain("Working set growth bytes:"));
                Assert.That(text, Does.Contain("Peak working set bytes:"));
                Assert.That(text, Does.Contain("Peak working set growth bytes:"));
                Assert.That(text, Does.Contain("Start managed memory bytes:"));
                Assert.That(text, Does.Contain("End managed memory bytes:"));
                Assert.That(text, Does.Contain("Managed memory growth bytes:"));
                Assert.That(text, Does.Contain("Peak managed memory bytes:"));
                Assert.That(text, Does.Contain("Peak managed memory growth bytes:"));
                Assert.That(text, Does.Contain("Iterations completed: 1"));
                Assert.That(text, Does.Contain("Iteration seconds total:"));
                Assert.That(text, Does.Contain("Iteration seconds average:"));
                Assert.That(text, Does.Contain("Iteration seconds max:"));
                Assert.That(text, Does.Contain("Final convergence activities: 0"));
                Assert.That(text, Does.Contain("Final state entries: 1"));
                Assert.That(text, Does.Contain("Sync errors: 0"));
                Assert.That(text, Does.Contain("Converged: yes"));
                Assert.That(text, Does.Contain("Failures: 0"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(handler.CreatedFileId));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(contentHash));
            });
        }

        [Test]
        public async Task SyncSoak_WithBrowserLoginRunsOneIterationAndPrintsSummary()
        {
            string localRoot = Path.Combine(_tempDirectory, "soak-browser-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "soak-browser.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from browser sync soak");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            File.SetLastWriteTimeUtc(localFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-soak-browser-state.db");
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
                    "sync-soak",
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
                    "--iterations",
                    "1",
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
                Assert.That(text, Does.Contain("Cotton Sync soak run"));
                Assert.That(text, Does.Contain("Iteration 1: activities=1, deferredLocalPaths=0, stateEntries=1"));
                Assert.That(text, Does.Contain("Final convergence activities: 0"));
                Assert.That(text, Does.Contain("Final state entries: 1"));
                Assert.That(text, Does.Contain("Converged: yes"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(contentHash));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Does.Contain("/api/v1/oauth/app-code/start"));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Does.Contain("/api/v1/oauth/app-code/poll"));
            });
        }

        [Test]
        public async Task SyncSoak_ReturnsFailureAndSummaryWhenSyncPassThrows()
        {
            string localRoot = Path.Combine(_tempDirectory, "soak-failing-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "soak-failing.txt";
            byte[] content = Encoding.UTF8.GetBytes("unexpected content hash");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            File.SetLastWriteTimeUtc(localFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string databasePath = Path.Combine(_tempDirectory, "sync-soak-failing-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(
                remoteRootId,
                relativePath,
                expectedContentHash: "unexpected-hash",
                expectedContent: content);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                    "--iterations",
                    "1",
                ],
                output,
                error,
                httpClient);

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Sync error: InvalidOperationException: Unexpected request:"));
                Assert.That(text, Does.Contain("Elapsed seconds:"));
                Assert.That(text, Does.Contain("CPU seconds:"));
                Assert.That(text, Does.Contain("CPU utilization percent:"));
                Assert.That(text, Does.Contain("Start working set bytes:"));
                Assert.That(text, Does.Contain("End working set bytes:"));
                Assert.That(text, Does.Contain("Working set growth bytes:"));
                Assert.That(text, Does.Contain("Peak working set bytes:"));
                Assert.That(text, Does.Contain("Peak working set growth bytes:"));
                Assert.That(text, Does.Contain("Start managed memory bytes:"));
                Assert.That(text, Does.Contain("End managed memory bytes:"));
                Assert.That(text, Does.Contain("Managed memory growth bytes:"));
                Assert.That(text, Does.Contain("Peak managed memory bytes:"));
                Assert.That(text, Does.Contain("Peak managed memory growth bytes:"));
                Assert.That(text, Does.Contain("Iterations completed: 0"));
                Assert.That(text, Does.Contain("Iteration seconds total: 0"));
                Assert.That(text, Does.Contain("Iteration seconds average: 0"));
                Assert.That(text, Does.Contain("Iteration seconds max: 0"));
                Assert.That(text, Does.Contain("Total activities: 0"));
                Assert.That(text, Does.Contain("Sync errors: 1"));
                Assert.That(text, Does.Contain("Final convergence activities: not run"));
                Assert.That(text, Does.Contain("Final state entries: not run"));
                Assert.That(text, Does.Contain("Converged: no"));
                Assert.That(text, Does.Contain("Failures: 1"));
            });
        }

        [Test]
        public async Task SyncSoak_RetriesFinalConvergenceUntilNoActivities()
        {
            string localRoot = Path.Combine(_tempDirectory, "soak-non-converged-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "soak-non-converged.txt";
            byte[] content = Encoding.UTF8.GetBytes("remote never reports this file");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            File.SetLastWriteTimeUtc(localFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "sync-soak-non-converged-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(
                remoteRootId,
                relativePath,
                contentHash,
                content,
                exposeCreatedFileInChildren: false);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                    "--iterations",
                    "1",
                ],
                output,
                error,
                httpClient);

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Iteration 1: activities=1, deferredLocalPaths=0, stateEntries=1"));
                Assert.That(text, Does.Contain("Final convergence activities: 0"));
                Assert.That(text, Does.Contain("Sync errors: 0"));
                Assert.That(text, Does.Contain("Converged: yes"));
                Assert.That(text, Does.Contain("Failures: 0"));
            });
        }

        [Test]
        public async Task SyncSoak_TwoClientModePropagatesClientAChangeToClientBAndConverges()
        {
            string firstLocalRoot = Path.Combine(_tempDirectory, "soak-two-client-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "soak-two-client-b");
            Directory.CreateDirectory(firstLocalRoot);
            Directory.CreateDirectory(secondLocalRoot);
            const string relativePath = "soak-two-client.txt";
            byte[] content = Encoding.UTF8.GetBytes("hello from client A");
            string firstLocalFilePath = Path.Combine(firstLocalRoot, relativePath);
            File.WriteAllBytes(firstLocalFilePath, content);
            File.SetLastWriteTimeUtc(firstLocalFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string firstDatabasePath = Path.Combine(_tempDirectory, "sync-soak-two-client-a.db");
            string secondDatabasePath = Path.Combine(_tempDirectory, "sync-soak-two-client-b.db");
            string firstSyncPairId = Guid.NewGuid().ToString("D");
            string secondSyncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceUploadServerHandler handler = new SyncOnceUploadServerHandler(remoteRootId, relativePath, contentHash, content);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
                    "--server",
                    "cotton.test",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    firstLocalRoot,
                    "--remote-root",
                    remoteRootId.ToString("D"),
                    "--sync-pair",
                    firstSyncPairId,
                    "--database",
                    firstDatabasePath,
                    "--iterations",
                    "1",
                    "--second-local-root",
                    secondLocalRoot,
                    "--second-sync-pair",
                    secondSyncPairId,
                    "--second-database",
                    secondDatabasePath,
                ],
                output,
                error,
                httpClient);

            SqliteSyncStateStore firstStore = new SqliteSyncStateStore(firstDatabasePath);
            SqliteSyncStateStore secondStore = new SqliteSyncStateStore(secondDatabasePath);
            SyncStateEntry? firstEntry = await firstStore.GetAsync(firstSyncPairId, relativePath);
            SyncStateEntry? secondEntry = await secondStore.GetAsync(secondSyncPairId, relativePath);
            string secondLocalFilePath = Path.Combine(secondLocalRoot, relativePath);
            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Cotton Sync soak run"));
                Assert.That(text, Does.Contain("Sync pair: " + firstSyncPairId));
                Assert.That(text, Does.Contain("Second sync pair: " + secondSyncPairId));
                Assert.That(
                    text,
                    Does.Contain("Iteration 1: clientAActivities=1, clientADeferredLocalPaths=0, clientBActivities=1, clientBDeferredLocalPaths=0, clientAStateEntries=1, clientBStateEntries=1"));
                Assert.That(text, Does.Contain("Total activities: 2"));
                Assert.That(text, Does.Contain("Final convergence activities: 0"));
                Assert.That(text, Does.Contain("Final state entries: 2"));
                Assert.That(text, Does.Contain("Converged: yes"));
                Assert.That(File.Exists(secondLocalFilePath), Is.True);
                Assert.That(File.ReadAllBytes(secondLocalFilePath), Is.EqualTo(content));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
                Assert.That(firstEntry!.RemoteFileId, Is.EqualTo(handler.CreatedFileId));
                Assert.That(secondEntry!.RemoteFileId, Is.EqualTo(handler.CreatedFileId));
                Assert.That(firstEntry.RemoteContentHash, Is.EqualTo(contentHash));
                Assert.That(secondEntry.RemoteContentHash, Is.EqualTo(contentHash));
            });
        }
    }
}
