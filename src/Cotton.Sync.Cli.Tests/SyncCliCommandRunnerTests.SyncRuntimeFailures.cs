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
        public async Task SyncOnce_ReturnsSupportableFailureWhenSyncPassTimesOut()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-timeout");
            Directory.CreateDirectory(localRoot);
            string databasePath = Path.Combine(_tempDirectory, "sync-timeout-state.db");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            string syncPairId = Guid.NewGuid().ToString("D");
            SyncOnceDirectoryServerHandler handler = new SyncOnceDirectoryServerHandler(
                remoteRootId,
                "unused",
                throwTimeoutOnChildren: true);
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

            string errorText = error.ToString();
            string outputText = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(outputText, Does.Contain("Transient sync failure: The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
                Assert.That(outputText, Does.Contain("Retrying attempt 2 of 3 after 1s."));
                Assert.That(outputText, Does.Contain("Retrying attempt 3 of 3 after 2s."));
                Assert.That(outputText, Does.Not.Contain("Cotton Sync one-shot run"));
                Assert.That(errorText, Does.Contain("sync-once failed."));
                Assert.That(errorText, Does.Contain("Server: https://cotton.test/"));
                Assert.That(errorText, Does.Contain("Local root: " + localRoot));
                Assert.That(errorText, Does.Contain("Remote root: " + remoteRootId.ToString("D")));
                Assert.That(errorText, Does.Contain("Sync pair: " + syncPairId));
                Assert.That(errorText, Does.Contain("Database: " + databasePath));
                Assert.That(errorText, Does.Contain("Error: The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
                Assert.That(errorText, Does.Not.Contain("Unhandled exception"));
                Assert.That(errorText, Does.Not.Contain(" at "));
                Assert.That(errorText, Does.Not.Contain(".cs:line"));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D") + "/children?page=1&pageSize=500&depth=0",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D") + "/children?page=1&pageSize=500&depth=0",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D") + "/children?page=1&pageSize=500&depth=0",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }

        [Test]
        public async Task SyncOnce_RetriesTransientTimeoutAndPersistsDirectoryBaseline()
        {
            string localRoot = Path.Combine(_tempDirectory, "local-transient-timeout");
            const string relativePath = "Projects";
            Directory.CreateDirectory(Path.Combine(localRoot, relativePath));
            string databasePath = Path.Combine(_tempDirectory, "sync-transient-timeout-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            SyncOnceDirectoryServerHandler handler = new SyncOnceDirectoryServerHandler(
                remoteRootId,
                relativePath,
                childrenTimeoutsBeforeSuccess: 1);
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
                Assert.That(text, Does.Contain("Transient sync failure: The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
                Assert.That(text, Does.Contain("Retrying attempt 2 of 3 after 1s."));
                Assert.That(text, Does.Contain("Cotton Sync one-shot run"));
                Assert.That(text, Does.Contain("Uploaded Projects - Created remote folder."));
                Assert.That(text, Does.Contain("State entries: 1"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.Kind, Is.EqualTo(SyncEntryKind.Directory));
                Assert.That(entry.RemoteNodeId, Is.EqualTo(handler.CreatedDirectoryId));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D") + "/children?page=1&pageSize=500&depth=0",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D") + "/children?page=1&pageSize=500&depth=0",
                    "/api/v1/layouts/nodes",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }

        [Test]
        public void SyncCliRunProgressWriter_SuppressesShortRunsAndWritesLongRunProgress()
        {
            StringWriter output = new StringWriter();
            SyncCliRunProgressWriter writer = new SyncCliRunProgressWriter(output);
            DateTime now = DateTime.UtcNow;
            writer.Report(new SyncRunProgress(
                SyncRunProgressStage.ScanningLocal,
                filesCompleted: 0,
                filesTotal: null,
                currentPath: null,
                startedAtUtc: now));

            writer.Report(new SyncRunProgress(
                SyncRunProgressStage.ReconcilingFiles,
                filesCompleted: 125,
                filesTotal: 10_000,
                currentPath: "phase511-10k-small-upload/file-00125.txt",
                startedAtUtc: now.AddSeconds(-30),
                bytesCompleted: 2_560,
                bytesTotal: 200_000));
            writer.Report(new SyncRunProgress(
                SyncRunProgressStage.CreatingPlaceholders,
                filesCompleted: 50,
                filesTotal: 1_000,
                currentPath: "remote-only.txt",
                startedAtUtc: now.AddSeconds(-30)));
            writer.Report(new SyncRunProgress(
                SyncRunProgressStage.DehydratingCloudFiles,
                filesCompleted: 300,
                filesTotal: 1_000,
                currentPath: "Music/track-00300.flac",
                startedAtUtc: now.AddSeconds(-30)));

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("Progress: reconciling files 125/10000 files"));
                Assert.That(text, Does.Contain("Progress: making cloud files available 50/1000 cloud items"));
                Assert.That(text, Does.Contain("Progress: freeing up space 300/1000 files"));
                Assert.That(text, Does.Contain("2.500 KiB/195.312 KiB"));
                Assert.That(text, Does.Contain("current: phase511-10k-small-upload/file-00125.txt"));
                Assert.That(text, Does.Contain("current: remote-only.txt"));
                Assert.That(text, Does.Contain("current: Music/track-00300.flac"));
                Assert.That(text, Does.Contain("elapsed: 00:00:30"));
                Assert.That(text, Does.Not.Contain("scanning local"));
            });
        }

        [Test]
        public async Task StateSummary_PrintsEntryCountAndCursor()
        {
            string databasePath = Path.Combine(_tempDirectory, "sync-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPairId,
                RelativePath = "Documents/report.txt",
                Kind = SyncEntryKind.File,
                SyncedAtUtc = DateTime.UtcNow,
            });
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = syncPairId,
                LastCursor = 42,
                UpdatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
            });
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                ["state-summary", "--database", databasePath, "--sync-pair", syncPairId],
                output,
                error);

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Entries: 1"));
                Assert.That(text, Does.Contain("Remote cursor: 42"));
                Assert.That(text, Does.Contain(syncPairId));
            });
        }

        [Test]
        public async Task StateSummary_ReturnsReadableErrorForCorruptDatabase()
        {
            string databasePath = Path.Combine(_tempDirectory, "corrupt-sync-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            await File.WriteAllTextAsync(databasePath, "not a sqlite database");
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                ["state-summary", "--database", databasePath, "--sync-pair", syncPairId],
                output,
                error);

            string errorText = error.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(errorText, Does.Contain("state-summary could not read the sync-state database"));
                Assert.That(errorText, Does.Contain("not a Cotton Sync state database"));
                Assert.That(errorText, Does.Not.Contain("Unhandled exception"));
                Assert.That(errorText, Does.Not.Contain("   at "));
            });
        }
    }
}
