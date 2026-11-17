// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Preferences;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [Test]
        public async Task LoadAsync_RetainsSessionAfterKeyringReadFailsAndRestoresOnRetry()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            await File.WriteAllTextAsync(paths.TokenStorePath, "protected-session-reference");
            SqliteAppPreferencesStore preferences = new(paths.AppDatabasePath);
            await preferences.InitializeAsync();
            await preferences.SaveAsync(new AppPreferences { RememberedServerUrl = serverUrl });
            FakeDesktopApplicationHost unavailable = FakeDesktopApplicationHost.Create(serverUrl);
            unavailable.TokenStore.ReadExceptions.Enqueue(new CryptographicException("Desktop keyring is unavailable."));
            FakeDesktopApplicationHost available = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(unavailable.Host, available.Host);
            await using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot waiting = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(waiting.IsSignedIn, Is.False);
                Assert.That(waiting.HasStoredSession, Is.True);
                Assert.That(waiting.StartupErrorMessage, Does.Contain("keyring"));
                Assert.That(unavailable.TokenStore.ClearAsyncCalls, Is.Zero);
                Assert.That(unavailable.App.RestoreSessionCalls, Is.Zero);
                Assert.That(unavailable.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });

            DesktopStoredSessionRestoreSnapshot restored = await controller.RestoreStoredSessionAsync(serverUrl.ToString());

            Assert.That(restored.Session, Is.Not.Null);
            Assert.That(available.TokenStore.ClearAsyncCalls, Is.Zero);
            Assert.That(File.Exists(paths.TokenStorePath), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task LoadAsync_RetainsSavedSessionUntilStorageBecomesAvailable(bool verificationTimesOut)
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            const string savedEnvelope = "protected-session-reference";
            await File.WriteAllTextAsync(paths.TokenStorePath, savedEnvelope);
            SqliteAppPreferencesStore preferences = new(paths.AppDatabasePath);
            await preferences.InitializeAsync();
            await preferences.SaveAsync(new AppPreferences { RememberedServerUrl = serverUrl });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(host.Host);
            bool storageLocked = true;
            await using DesktopShellController controller = CreateController(
                paths,
                factory,
                tokenStorageVerifier: async token =>
                {
                    if (!storageLocked)
                    {
                        return CreateSecureTokenStorage();
                    }

                    if (verificationTimesOut)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }

                    return CreateInsecureTokenStorage();
                },
                tokenStorageVerificationTimeout: TimeSpan.FromMilliseconds(50));

            DesktopShellSnapshot waiting = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(waiting.IsSignedIn, Is.False);
                Assert.That(waiting.HasStoredSession, Is.True, "A locked keyring must keep automatic restoration enabled.");
                Assert.That(waiting.StartupErrorMessage, Is.Not.Empty);
                Assert.That(factory.CreatedServerUrls, Is.Empty);
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.Zero);
            });
            Assert.That(await File.ReadAllTextAsync(paths.TokenStorePath), Is.EqualTo(savedEnvelope));

            storageLocked = false;
            DesktopStoredSessionRestoreSnapshot restored = await controller.RestoreStoredSessionAsync(serverUrl.ToString());

            Assert.Multiple(() =>
            {
                Assert.That(restored.Session, Is.Not.Null);
                Assert.That(restored.HasStoredSession, Is.True);
                Assert.That(restored.ErrorMessage, Is.Null);
                Assert.That(host.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.Zero);
            });
        }
    }
}
