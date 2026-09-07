// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task SignInLiveSmokeClientsAsync_UsesOnlySelectedAuthenticationMode(bool usePassword)
        {
            string variableName = "COTTON_TEST_PASSWORD_" + Guid.NewGuid().ToString("N");
            const string password = " test password with spaces ";
            Environment.SetEnvironmentVariable(variableName, password);
            var (session, firstHost, secondHost) = CreateUnsignedLiveSmokeSession();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            using StringWriter output = new();
            try
            {
                List<string> arguments = ["--live-sync-smoke", "--server", "https://cotton.example.test/", "--username", "demo-user"];
                if (usePassword)
                {
                    arguments.AddRange(["--live-sync-smoke-password-env", variableName]);
                }

                await DesktopCommandLineRunner.SignInLiveSmokeClientsAsync(
                    DesktopStartupOptions.Parse(arguments), session, output, CancellationToken.None);

                Assert.Multiple(() =>
                {
                    Assert.That(session.FirstSignedIn, Is.True);
                    Assert.That(session.SecondSignedIn, Is.True);
                    Assert.That(firstHost.App.PasswordSignInCalls, Is.EqualTo(usePassword ? 1 : 0));
                    Assert.That(secondHost.App.PasswordSignInCalls, Is.EqualTo(usePassword ? 1 : 0));
                    Assert.That(firstHost.App.BrowserSignInCalls, Is.EqualTo(usePassword ? 0 : 1));
                    Assert.That(secondHost.App.BrowserSignInCalls, Is.EqualTo(usePassword ? 0 : 1));
                    Assert.That(output.ToString(), Does.Not.Contain(password));
                    Assert.That(output.ToString(), Does.Not.Contain(variableName));
                });
                if (usePassword)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(firstHost.App.LastPasswordSignInRequest?.Username, Is.EqualTo("demo-user"));
                        Assert.That(firstHost.App.LastPasswordSignInRequest?.Password, Is.EqualTo(password));
                        Assert.That(secondHost.App.LastPasswordSignInRequest?.Password, Is.EqualTo(password));
                    });
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }

        [Test]
        public async Task SignInLiveSmokeClientsAsync_DoesNotFallBackWhenSecondPasswordLoginFails()
        {
            string variableName = "COTTON_TEST_PASSWORD_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variableName, "test-password");
            var (session, firstHost, secondHost) = CreateUnsignedLiveSmokeSession();
            await using DesktopShellController firstController = session.FirstController;
            await using DesktopShellController secondController = session.SecondController;
            secondHost.App.PasswordSignInException = new InvalidOperationException("password login rejected");
            using StringWriter output = new();
            try
            {
                DesktopStartupOptions options = DesktopStartupOptions.Parse(
                    ["--live-sync-smoke", "--server", "https://cotton.example.test/", "--username", "demo-user",
                        "--live-sync-smoke-password-env", variableName]);

                Assert.ThrowsAsync<InvalidOperationException>(() => DesktopCommandLineRunner.SignInLiveSmokeClientsAsync(
                    options, session, output, CancellationToken.None));

                Assert.Multiple(() =>
                {
                    Assert.That(session.FirstSignedIn, Is.True);
                    Assert.That(session.SecondSignedIn, Is.False);
                    Assert.That(firstHost.App.PasswordSignInCalls, Is.EqualTo(1));
                    Assert.That(secondHost.App.PasswordSignInCalls, Is.EqualTo(1));
                    Assert.That(firstHost.App.BrowserSignInCalls, Is.Zero);
                    Assert.That(secondHost.App.BrowserSignInCalls, Is.Zero);
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }

        private (DesktopLiveSyncSmokeSession Session, FakeDesktopApplicationHost FirstHost,
            FakeDesktopApplicationHost SecondHost) CreateUnsignedLiveSmokeSession()
        {
            Uri serverUrl = new("https://cotton.example.test/");
            DesktopAppPaths firstPaths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "first"));
            DesktopAppPaths secondPaths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "second"));
            FakeDesktopApplicationHost firstHost = FakeDesktopApplicationHost.Create(serverUrl);
            FakeDesktopApplicationHost secondHost = FakeDesktopApplicationHost.Create(serverUrl);
            DesktopLiveSyncSmokeSession session = new(firstPaths, secondPaths,
                CreateController(firstPaths, new QueueingDesktopSyncApplicationFactory(firstHost.Host)),
                CreateController(secondPaths, new QueueingDesktopSyncApplicationFactory(secondHost.Host)));
            return (session, firstHost, secondHost);
        }
    }
}
