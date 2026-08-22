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
        public async Task SyncOnce_ExternalProcessRecoversAfterRemoteUploadBeforeBaselineUpdate()
        {
            string localRoot = Path.Combine(_tempDirectory, "process-crash-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "crash-recovery.txt";
            byte[] content = Encoding.UTF8.GetBytes("uploaded before process crash");
            string localFilePath = Path.Combine(localRoot, relativePath);
            File.WriteAllBytes(localFilePath, content);
            File.SetLastWriteTimeUtc(localFilePath, new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc));
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "process-crash-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            await using SyncProcessCrashHttpServer server = new SyncProcessCrashHttpServer(remoteRootId, relativePath, contentHash, content);
            string[] args = CreateSyncOnceProcessArgs(server.BaseUri, localRoot, remoteRootId, syncPairId, databasePath);

            using Process crashingProcess = StartCliProcess(args);
            Task<string> firstOutputTask = crashingProcess.StandardOutput.ReadToEndAsync();
            Task<string> firstErrorTask = crashingProcess.StandardError.ReadToEndAsync();
            try
            {
                await server.WaitForFileCommittedAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                KillProcessTree(crashingProcess);
                await WaitForProcessExitAsync(crashingProcess, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            finally
            {
                server.ReleaseBlockedCreateResponse();
                KillProcessTree(crashingProcess);
            }

            _ = await firstOutputTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _ = await firstErrorTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            SqliteSyncStateStore storeAfterCrash = new SqliteSyncStateStore(databasePath);
            IReadOnlyList<SyncStateEntry> entriesAfterCrash = await storeAfterCrash.LoadPairAsync(syncPairId);

            using Process recoveryProcess = StartCliProcess(args);
            Task<string> recoveryOutputTask = recoveryProcess.StandardOutput.ReadToEndAsync();
            Task<string> recoveryErrorTask = recoveryProcess.StandardError.ReadToEndAsync();
            await WaitForProcessExitAsync(recoveryProcess, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            string recoveryOutput = await recoveryOutputTask.ConfigureAwait(false);
            string recoveryError = await recoveryErrorTask.ConfigureAwait(false);

            SqliteSyncStateStore storeAfterRecovery = new SqliteSyncStateStore(databasePath);
            SyncStateEntry? entry = await storeAfterRecovery.GetAsync(syncPairId, relativePath);
            IReadOnlyList<HttpRequestSnapshot> requests = server.Requests;
            server.AssertNoFaults();

            Assert.Multiple(() =>
            {
                Assert.That(crashingProcess.ExitCode, Is.Not.EqualTo(0));
                Assert.That(entriesAfterCrash, Is.Empty);
                Assert.That(recoveryProcess.ExitCode, Is.EqualTo(0), recoveryError);
                Assert.That(recoveryError, Is.Empty);
                Assert.That(recoveryOutput, Does.Contain("Activities: 0"));
                Assert.That(recoveryOutput, Does.Contain("State entries: 1"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.Kind, Is.EqualTo(SyncEntryKind.File));
                Assert.That(entry.LocalContentHash, Is.EqualTo(contentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(contentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(server.CreatedFileId));
                Assert.That(
                    requests.Count(static request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/files/from-chunks"),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SyncOnce_ExternalProcessRecoversAfterCrashDuringRemoteDownload()
        {
            string localRoot = Path.Combine(_tempDirectory, "process-download-crash-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "remote-download-crash.txt";
            byte[] content = Encoding.UTF8.GetBytes("download interrupted before the first process can finish");
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "process-download-crash-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            string targetPath = Path.Combine(localRoot, relativePath);
            string temporaryDirectory = Path.Combine(localRoot, ".cotton-sync", "tmp");
            await using SyncProcessDownloadCrashHttpServer server = new SyncProcessDownloadCrashHttpServer(remoteRootId, relativePath, contentHash, content);
            string[] args = CreateSyncOnceProcessArgs(server.BaseUri, localRoot, remoteRootId, syncPairId, databasePath);

            using Process crashingProcess = StartCliProcess(args);
            Task<string> firstOutputTask = crashingProcess.StandardOutput.ReadToEndAsync();
            Task<string> firstErrorTask = crashingProcess.StandardError.ReadToEndAsync();
            try
            {
                Task downloadStarted = server.WaitForFirstDownloadStartedAsync(TimeSpan.FromSeconds(10));
                await WaitForServerSignalAsync(
                        downloadStarted,
                        crashingProcess,
                        firstOutputTask,
                        firstErrorTask,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await WaitForTemporaryDownloadAsync(temporaryDirectory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                KillProcessTree(crashingProcess);
                await WaitForProcessExitAsync(crashingProcess, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            finally
            {
                server.ReleaseFirstDownload();
                KillProcessTree(crashingProcess);
            }

            _ = await firstOutputTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _ = await firstErrorTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            SyncStateEntry? entryAfterCrash = await store.GetAsync(syncPairId, relativePath);
            string[] staleTemporaryFiles = ListTemporaryDownloads(temporaryDirectory);
            bool targetExistsAfterCrash = File.Exists(targetPath);

            using Process recoveryProcess = StartCliProcess(args);
            Task<string> recoveryOutputTask = recoveryProcess.StandardOutput.ReadToEndAsync();
            Task<string> recoveryErrorTask = recoveryProcess.StandardError.ReadToEndAsync();
            await WaitForProcessExitAsync(recoveryProcess, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            string recoveryOutput = await recoveryOutputTask.ConfigureAwait(false);
            string recoveryError = await recoveryErrorTask.ConfigureAwait(false);

            SyncStateEntry? entryAfterRecovery = await store.GetAsync(syncPairId, relativePath);
            string[] remainingTemporaryFiles = ListTemporaryDownloads(temporaryDirectory);
            IReadOnlyList<HttpRequestSnapshot> requests = server.Requests;
            string downloadPath = "/api/v1/files/" + server.RemoteFileId.ToString("D") + "/content?download=false";
            server.AssertNoFaults();

            Assert.Multiple(() =>
            {
                Assert.That(crashingProcess.ExitCode, Is.Not.EqualTo(0));
                Assert.That(targetExistsAfterCrash, Is.False);
                Assert.That(entryAfterCrash, Is.Null);
                Assert.That(staleTemporaryFiles, Is.Not.Empty);
                Assert.That(recoveryProcess.ExitCode, Is.EqualTo(0), recoveryError);
                Assert.That(recoveryError, Is.Empty);
                Assert.That(recoveryOutput, Does.Contain("Downloaded remote-download-crash.txt"));
                Assert.That(recoveryOutput, Does.Contain("State entries: 1"));
                Assert.That(File.ReadAllBytes(targetPath), Is.EqualTo(content));
                Assert.That(entryAfterRecovery, Is.Not.Null);
                Assert.That(entryAfterRecovery!.RemoteFileId, Is.EqualTo(server.RemoteFileId));
                Assert.That(entryAfterRecovery.RemoteContentHash, Is.EqualTo(contentHash));
                Assert.That(remainingTemporaryFiles, Is.Empty);
                Assert.That(
                    requests.Count(request => request.Method == HttpMethod.Get && request.PathAndQuery == downloadPath),
                    Is.EqualTo(2));
            });
        }

        [Test]
        public async Task SyncOnce_ExternalProcessRecoversAfterRemoteDeleteBeforeBaselineDelete()
        {
            string localRoot = Path.Combine(_tempDirectory, "process-delete-crash-local");
            Directory.CreateDirectory(localRoot);
            const string relativePath = "remote-delete-crash.txt";
            byte[] content = Encoding.UTF8.GetBytes("remote delete before baseline");
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            string databasePath = Path.Combine(_tempDirectory, "process-delete-crash-state.db");
            string syncPairId = Guid.NewGuid().ToString("D");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            await using SyncProcessRemoteDeleteCrashHttpServer server = new SyncProcessRemoteDeleteCrashHttpServer(remoteRootId, relativePath, contentHash);
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPairId,
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = contentHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc),
                RemoteFileId = server.RemoteFileId,
                RemoteContentHash = contentHash,
                RemoteETag = "sha256-" + contentHash,
                SyncedAtUtc = new DateTime(2026, 6, 4, 12, 1, 0, DateTimeKind.Utc),
            });
            string[] args = CreateSyncOnceProcessArgs(server.BaseUri, localRoot, remoteRootId, syncPairId, databasePath);

            using Process crashingProcess = StartCliProcess(args);
            Task<string> firstOutputTask = crashingProcess.StandardOutput.ReadToEndAsync();
            Task<string> firstErrorTask = crashingProcess.StandardError.ReadToEndAsync();
            try
            {
                await server.WaitForFileDeletedAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                KillProcessTree(crashingProcess);
                await WaitForProcessExitAsync(crashingProcess, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            finally
            {
                server.ReleaseBlockedDeleteResponse();
                KillProcessTree(crashingProcess);
            }

            _ = await firstOutputTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _ = await firstErrorTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            SyncStateEntry? entryAfterCrash = await store.GetAsync(syncPairId, relativePath);

            using Process recoveryProcess = StartCliProcess(args);
            Task<string> recoveryOutputTask = recoveryProcess.StandardOutput.ReadToEndAsync();
            Task<string> recoveryErrorTask = recoveryProcess.StandardError.ReadToEndAsync();
            await WaitForProcessExitAsync(recoveryProcess, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            string recoveryOutput = await recoveryOutputTask.ConfigureAwait(false);
            string recoveryError = await recoveryErrorTask.ConfigureAwait(false);

            IReadOnlyList<SyncStateEntry> entriesAfterRecovery = await store.LoadPairAsync(syncPairId);
            IReadOnlyList<HttpRequestSnapshot> requests = server.Requests;
            string deletePath = "/api/v1/files/" + server.RemoteFileId.ToString("D") + "?skipTrash=false";
            server.AssertNoFaults();

            Assert.Multiple(() =>
            {
                Assert.That(crashingProcess.ExitCode, Is.Not.EqualTo(0));
                Assert.That(entryAfterCrash, Is.Not.Null);
                Assert.That(entryAfterCrash!.RemoteFileId, Is.EqualTo(server.RemoteFileId));
                Assert.That(recoveryProcess.ExitCode, Is.EqualTo(0), recoveryError);
                Assert.That(recoveryError, Is.Empty);
                Assert.That(recoveryOutput, Does.Contain("Activities: 0"));
                Assert.That(recoveryOutput, Does.Contain("State entries: 0"));
                Assert.That(entriesAfterRecovery, Is.Empty);
                Assert.That(
                    requests.Count(request => request.Method == HttpMethod.Delete && request.PathAndQuery == deletePath),
                    Is.EqualTo(1));
            });
        }
    }
}
