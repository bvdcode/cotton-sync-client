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
        public async Task RunAsync_ReturnsErrorForMissingStateSummaryArguments()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(["state-summary"], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--database"));
                Assert.That(error.ToString(), Does.Contain("--sync-pair"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForMissingSyncOnceArguments()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(["sync-once"], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--server"));
                Assert.That(error.ToString(), Does.Contain("--remote-root"));
                Assert.That(error.ToString(), Does.Contain("--database"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForMissingSyncSoakLimiter()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                Assert.That(error.ToString(), Does.Contain("--iterations"));
                Assert.That(error.ToString(), Does.Contain("--duration-seconds"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForMissingSyncCrudSmokeSecondClientArguments()
        {
            string localRoot = Path.Combine(_tempDirectory, "crud-local-a");
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-crud-smoke",
                    "--server",
                    "https://cloud.example.test/",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    localRoot,
                    "--remote-root",
                    Guid.NewGuid().ToString("D"),
                    "--sync-pair",
                    Guid.NewGuid().ToString("D"),
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state-a.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--second-local-root"));
                Assert.That(error.ToString(), Does.Contain("--second-sync-pair"));
                Assert.That(error.ToString(), Does.Contain("--second-database"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForNonEmptySyncCrudSmokeLocalRoot()
        {
            string firstLocalRoot = Path.Combine(_tempDirectory, "crud-local-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "crud-local-b");
            Directory.CreateDirectory(firstLocalRoot);
            Directory.CreateDirectory(secondLocalRoot);
            await File.WriteAllTextAsync(Path.Combine(firstLocalRoot, "existing.txt"), "do not touch");
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-crud-smoke",
                    "--server",
                    "https://cloud.example.test/",
                    "--username",
                    "testuser",
                    "--password",
                    "testpassword",
                    "--local-root",
                    firstLocalRoot,
                    "--remote-root",
                    Guid.NewGuid().ToString("D"),
                    "--sync-pair",
                    Guid.NewGuid().ToString("D"),
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state-a.db"),
                    "--second-local-root",
                    secondLocalRoot,
                    "--second-sync-pair",
                    Guid.NewGuid().ToString("D"),
                    "--second-database",
                    Path.Combine(_tempDirectory, "sync-state-b.db"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--local-root must be empty or missing"));
            });
        }

        [Test]
        public void SyncCrudSmoke_InitialConvergenceLinePassesOnlyWhenBothClientsConverged()
        {
            SyncCliConvergenceResult first = CreateCrudSmokeConvergence(converged: true);
            SyncCliConvergenceResult second = CreateCrudSmokeConvergence(converged: true);

            string line = SyncCliCrudSmokeCommandRunner.FormatInitialConvergenceLine(first, second);

            Assert.Multiple(() =>
            {
                Assert.That(line, Does.StartWith("PASS: Initial sync reached idle/up-to-date."));
                Assert.That(line, Does.Contain("clientAActivities=0"));
                Assert.That(line, Does.Contain("clientADeferredLocalPaths=0"));
                Assert.That(line, Does.Contain("clientAConverged=yes"));
                Assert.That(line, Does.Contain("clientBActivities=0"));
                Assert.That(line, Does.Contain("clientBDeferredLocalPaths=0"));
                Assert.That(line, Does.Contain("clientBConverged=yes"));
            });
        }

        [Test]
        public void SyncCrudSmoke_InitialConvergenceLineFailsWhenActivitiesRemain()
        {
            SyncRunResult activeResult = new();
            activeResult.RecordActivity(
                new SyncActivity
                {
                    Kind = SyncActivityKind.Uploaded,
                    RelativePath = "still-changing.txt",
                },
                maximumStoredActivities: 1);
            SyncCliConvergenceResult first = new(new SyncCliPassResult(activeResult, []), Converged: false, Passes: 6);
            SyncCliConvergenceResult second = CreateCrudSmokeConvergence(converged: true);

            string line = SyncCliCrudSmokeCommandRunner.FormatInitialConvergenceLine(first, second);

            Assert.Multiple(() =>
            {
                Assert.That(line, Does.StartWith("FAIL: Initial sync reached idle/up-to-date."));
                Assert.That(line, Does.Contain("clientAActivities=1"));
                Assert.That(line, Does.Contain("clientAConverged=no"));
                Assert.That(line, Does.Contain("clientBConverged=yes"));
            });
        }

        [Test]
        public void SyncCrudSmoke_InitialConvergenceLineFailsWhenDeferredLocalPathsRemain()
        {
            SyncRunResult deferredResult = new();
            deferredResult.RecordDeferredLocalPath("fresh-file.txt");
            SyncCliConvergenceResult first = new(new SyncCliPassResult(deferredResult, []), Converged: false, Passes: 6);
            SyncCliConvergenceResult second = CreateCrudSmokeConvergence(converged: true);

            string line = SyncCliCrudSmokeCommandRunner.FormatInitialConvergenceLine(first, second);

            Assert.Multiple(() =>
            {
                Assert.That(line, Does.StartWith("FAIL: Initial sync reached idle/up-to-date."));
                Assert.That(line, Does.Contain("clientAActivities=0"));
                Assert.That(line, Does.Contain("clientADeferredLocalPaths=1"));
                Assert.That(line, Does.Contain("clientAConverged=no"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForInvalidSyncSoakProbeFile()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                    "--sync-pair",
                    "pair",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state.db"),
                    "--iterations",
                    "1",
                    "--probe-file",
                    "../outside.txt",
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--probe-file"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForIncompleteSyncSoakSecondClient()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "sync-soak",
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
                    "--sync-pair",
                    "pair-a",
                    "--database",
                    Path.Combine(_tempDirectory, "sync-state-a.db"),
                    "--iterations",
                    "1",
                    "--second-local-root",
                    Path.Combine(_tempDirectory, "second"),
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--second-local-root"));
                Assert.That(error.ToString(), Does.Contain("--second-sync-pair"));
                Assert.That(error.ToString(), Does.Contain("--second-database"));
            });
        }

        [Test]
        public async Task RunAsync_ReturnsErrorForUnsafeSyncSoakSecondClientInputs()
        {
            string firstLocalRoot = Path.Combine(_tempDirectory, "client-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "client-b");
            string nestedSecondLocalRoot = Path.Combine(firstLocalRoot, "nested");
            string firstDatabasePath = Path.Combine(_tempDirectory, "client-a.db");
            string secondDatabasePath = Path.Combine(_tempDirectory, "client-b.db");
            var cases = new[]
            {
                new
                {
                    SecondLocalRoot = firstLocalRoot,
                    SecondSyncPairId = "pair-b",
                    SecondDatabasePath = secondDatabasePath,
                    ExpectedMessage = "local roots",
                },
                new
                {
                    SecondLocalRoot = nestedSecondLocalRoot,
                    SecondSyncPairId = "pair-b",
                    SecondDatabasePath = secondDatabasePath,
                    ExpectedMessage = "local roots",
                },
                new
                {
                    SecondLocalRoot = secondLocalRoot,
                    SecondSyncPairId = "pair-a",
                    SecondDatabasePath = secondDatabasePath,
                    ExpectedMessage = "sync pair ids",
                },
                new
                {
                    SecondLocalRoot = secondLocalRoot,
                    SecondSyncPairId = "pair-b",
                    SecondDatabasePath = firstDatabasePath,
                    ExpectedMessage = "databases",
                },
            };

            foreach (var testCase in cases)
            {
                using StringWriter output = new StringWriter();
                using StringWriter error = new StringWriter();

                int exitCode = await SyncCliCommandRunner.RunAsync(
                    [
                        "sync-soak",
                        "--server",
                        "https://cloud.example.test/",
                        "--username",
                        "testuser",
                        "--password",
                        "testpassword",
                        "--local-root",
                        firstLocalRoot,
                        "--remote-root",
                        Guid.NewGuid().ToString("D"),
                        "--sync-pair",
                        "pair-a",
                        "--database",
                        firstDatabasePath,
                        "--iterations",
                        "1",
                        "--second-local-root",
                        testCase.SecondLocalRoot,
                        "--second-sync-pair",
                        testCase.SecondSyncPairId,
                        "--second-database",
                        testCase.SecondDatabasePath,
                    ],
                    output,
                    error);

                Assert.Multiple(() =>
                {
                    Assert.That(exitCode, Is.EqualTo(2));
                    Assert.That(output.ToString(), Is.Empty);
                    Assert.That(error.ToString(), Does.Contain(testCase.ExpectedMessage));
                });
            }
        }
    }
}
