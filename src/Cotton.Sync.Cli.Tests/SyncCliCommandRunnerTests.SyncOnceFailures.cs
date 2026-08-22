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
        public async Task RunAsync_ReturnsErrorForInvalidSyncOnceRemoteRoot()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "https://cloud.example.test/",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    _tempDirectory,
                    "--remote-root",
                    "not-a-guid",
                    "--sync-pair",
                    "pair",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--remote-root"));
                Assert.That(error.ToString(), Does.Contain("GUID"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorWhenSyncOnceRemoteRootAndRemotePathAreBothProvided()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "https://cloud.example.test/",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    _tempDirectory,
                    "--remote-root",
                    Guid.NewGuid().ToString("D"),
                    "--remote-path",
                    "/Desktop",
                    "--sync-pair",
                    "pair",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--remote-root"));
                Assert.That(error.ToString(), Does.Contain("--remote-path"));
                Assert.That(error.ToString(), Does.Contain("cannot be used together"));
            });
        }

        [Test]
        public async Task RunAsync_AcceptsBareSyncOnceServerHostBeforeRemoteRootValidation()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "app.cottoncloud.dev",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    _tempDirectory,
                    "--remote-root",
                    "not-a-guid",
                    "--sync-pair",
                    "pair",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--remote-root"));
                Assert.That(error.ToString(), Does.Contain("GUID"));
                Assert.That(error.ToString(), Does.Not.Contain("--server"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForUnsupportedSyncOnceServerScheme()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-once",
                    "--server",
                    "ftp://app.cottoncloud.dev",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    _tempDirectory,
                    "--remote-root",
                    Guid.NewGuid().ToString("D"),
                    "--sync-pair",
                    "pair",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--server"));
                Assert.That(error.ToString(), Does.Contain("HTTP or HTTPS"));
            });
        }

        [Test]
        public async Task SyncOnce_RemoteRootNotFoundReturnsSupportableErrorWithoutStackTrace()
        {
            string localRoot = Path.Combine(_tempDirectory, "missing-remote-root-local");
            Directory.CreateDirectory(localRoot);
            File.WriteAllText(Path.Combine(localRoot, "file.txt"), "content");
            string databasePath = Path.Combine(_tempDirectory, "missing-remote-root-state.db");
            Guid remoteRootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            RemoteRootNotFoundServerHandler handler = new RemoteRootNotFoundServerHandler(remoteRootId);
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
                    "pair",
                    "--database",
                    databasePath,
                ],
                output,
                error,
                httpClient);

            string errorText = error.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(errorText, Does.Contain("sync-once failed."));
                Assert.That(errorText, Does.Contain("Server: https://cotton.test/"));
                Assert.That(errorText, Does.Contain("Local root: " + localRoot));
                Assert.That(errorText, Does.Contain("Remote root: " + remoteRootId.ToString("D")));
                Assert.That(errorText, Does.Contain("Sync pair: pair"));
                Assert.That(errorText, Does.Contain("Database: " + databasePath));
                Assert.That(errorText, Does.Contain("Error: Cotton API returned 404 NotFound."));
                Assert.That(errorText, Does.Not.Contain(" at "));
                Assert.That(errorText, Does.Not.Contain("RemoteTreeCrawler"));
                Assert.That(errorText, Does.Not.Contain(".cs:line"));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/auth/login",
                    "/api/v1/layouts/nodes/" + remoteRootId.ToString("D"),
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
            });
        }
    }
}
