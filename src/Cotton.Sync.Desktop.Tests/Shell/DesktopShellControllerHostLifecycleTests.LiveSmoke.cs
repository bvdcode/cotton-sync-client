// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [TestCase(true, false, false, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(false, false, false, true)]
        [TestCase(true, true, true, true)]
        public async Task RunLiveSyncSmokeSessionAsync_FailsWhenCleanupFailsAndAttemptsEveryStep(
            bool firstRemovalFails,
            bool secondRemovalFails,
            bool firstSignOutFails,
            bool secondSignOutFails)
        {
            var (session, firstHost, secondHost) = await CreateLiveSmokeSessionAsync();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            firstHost.App.DeleteSyncPairException = firstRemovalFails ? new IOException("first removal failed") : null;
            secondHost.App.DeleteSyncPairException = secondRemovalFails ? new IOException("second removal failed") : null;
            firstHost.App.SignOutException = firstSignOutFails ? new IOException("first sign-out failed") : null;
            secondHost.App.SignOutException = secondSignOutFails ? new IOException("second sign-out failed") : null;
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeSessionAsync(
                session, () => Task.FromResult(0), output);

            int expectedFailures = new[]
            {
                firstRemovalFails, secondRemovalFails, firstSignOutFails, secondSignOutFails,
            }.Count(static failed => failed);
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(firstHost.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(firstHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failures: " + expectedFailures));
                Assert.That(output.ToString(), Does.EndWith("Result: failed" + Environment.NewLine));
                Assert.That(output.ToString(), Does.Not.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeSessionAsync_PassesOnlyAfterBothPairsAndSessionsAreRemoved()
        {
            var (session, firstHost, secondHost) = await CreateLiveSmokeSessionAsync();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeSessionAsync(
                session, () => Task.FromResult(0), output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(firstHost.App.DeletedSyncPairId, Is.EqualTo(session.FirstPair!.Id));
                Assert.That(secondHost.App.DeletedSyncPairId, Is.EqualTo(session.SecondPair!.Id));
                Assert.That(firstHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Removed first live-smoke sync pair:"));
                Assert.That(output.ToString(), Does.Contain("Removed second live-smoke sync pair:"));
                Assert.That(output.ToString(), Does.EndWith(
                    "Signed out second desktop client." + Environment.NewLine
                    + "Failures: 0" + Environment.NewLine + "Result: passed" + Environment.NewLine));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeSessionAsync_PreservesWorkflowFailuresWhenCleanupAlsoFails()
        {
            var (session, firstHost, _) = await CreateLiveSmokeSessionAsync();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            firstHost.App.DeleteSyncPairException = new IOException("removal failed");
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeSessionAsync(
                session, () => Task.FromResult(2), output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failures: 3"));
                Assert.That(output.ToString(), Does.EndWith("Result: failed" + Environment.NewLine));
                Assert.That(output.ToString(), Does.Not.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeSessionAsync_PreservesWorkflowErrorWhenCleanupAlsoFails()
        {
            var (session, firstHost, secondHost) = await CreateLiveSmokeSessionAsync();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            firstHost.App.DeleteSyncPairException = new IOException("removal failed");
            secondHost.App.SignOutException = new IOException("sign-out failed");
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeSessionAsync(
                session,
                () => Task.FromException<int>(new InvalidOperationException("workflow failed\noriginal detail")),
                output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Error: InvalidOperationException: workflow failed original detail"));
                Assert.That(output.ToString(), Does.Contain("removal failed"));
                Assert.That(output.ToString(), Does.Contain("sign-out failed"));
                Assert.That(output.ToString(), Does.Contain("Failures: 3"));
                Assert.That(output.ToString(), Does.EndWith("Result: failed" + Environment.NewLine));
                Assert.That(output.ToString(), Does.Not.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeSessionAsync_PreservesCancellationWhileAttemptingCleanup()
        {
            var (session, firstHost, secondHost) = await CreateLiveSmokeSessionAsync();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            firstHost.App.DeleteSyncPairException = new IOException("removal failed");
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            OperationCanceledException originalException = new(cancellation.Token);
            using StringWriter output = new();

            OperationCanceledException? exception = Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await DesktopCommandLineRunner.RunLiveSyncSmokeSessionAsync(
                    session,
                    () => Task.FromException<int>(originalException),
                    output,
                    cancellation.Token));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.SameAs(originalException));
                Assert.That(firstHost.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(firstHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.SignOutCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("removal failed"));
                Assert.That(output.ToString(), Does.Not.Contain("Result: passed"));
            });
        }

        private async Task<(DesktopLiveSyncSmokeSession Session, FakeDesktopApplicationHost FirstHost,
            FakeDesktopApplicationHost SecondHost)> CreateLiveSmokeSessionAsync()
        {
            Uri serverUrl = new("https://cotton.example.test/");
            DesktopAppPaths firstPaths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "first"));
            DesktopAppPaths secondPaths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "second"));
            FakeDesktopApplicationHost firstHost = FakeDesktopApplicationHost.Create(serverUrl);
            FakeDesktopApplicationHost secondHost = FakeDesktopApplicationHost.Create(serverUrl);
            firstHost.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondHost.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DesktopShellController firstController = CreateController(
                firstPaths, new QueueingDesktopSyncApplicationFactory(firstHost.Host));
            DesktopShellController secondController = CreateController(
                secondPaths, new QueueingDesktopSyncApplicationFactory(secondHost.Host));
            await firstController.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            await secondController.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            await firstHost.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await secondHost.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            DesktopLiveSyncSmokeSession session = new(firstPaths, secondPaths, firstController, secondController)
            {
                FirstPair = CreateSyncPair(isEnabled: true),
                SecondPair = CreateSyncPair(isEnabled: true),
                FirstSignedIn = true,
                SecondSignedIn = true,
            };
            return (session, firstHost, secondHost);
        }
    }
}
