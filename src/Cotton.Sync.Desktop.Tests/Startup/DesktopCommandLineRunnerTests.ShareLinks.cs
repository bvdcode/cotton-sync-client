// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopCommandLineRunnerTests
    {
        [Test]
        public async Task RunShellShareLinkTargetAsync_CreatesShareLinkForSyncedFileWithoutPrintingLocalPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            Uri shareLink = new Uri("https://cloud.example/s/generated-token");
            SqliteSyncPairSettingsStore pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Docs/report.pdf",
                Kind = SyncEntryKind.File,
                RemoteNodeId = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                SyncedAtUtc = new DateTime(2026, 06, 20, 12, 00, 00, DateTimeKind.Utc),
            });
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--resolve-shell-share-link-target", selectedPath]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkTargetAsync(
                paths,
                options,
                output,
                shareLinkClient: new FakeDesktopShellShareLinkClient(
                    DesktopShellShareLinkResult.Created(shareLink)));

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(report, Does.Contain("Cotton Sync Desktop shell share-link target"));
                Assert.That(report, Does.Contain("Status: resolved"));
                Assert.That(report, Does.Contain("TargetResolved: true"));
                Assert.That(report, Does.Contain("TargetHasRemoteIdentity: true"));
                Assert.That(report, Does.Contain("ShareLinkApi: available"));
                Assert.That(report, Does.Contain("CanCreateShareLink: true"));
                Assert.That(report, Does.Contain("ShareLinkCreated: true"));
                Assert.That(report, Does.Contain("ShareLink: " + shareLink.AbsoluteUri));
                Assert.That(report, Does.Contain("TargetKind: file"));
                Assert.That(report, Does.Contain("HasSyncPair: true"));
                Assert.That(report, Does.Contain("HasRemoteFileId: true"));
                Assert.That(report, Does.Contain("Result: passed"));
                Assert.That(report, Does.Not.Contain("FailureReason:"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("report.pdf"));
            });
        }

        [Test]
        public async Task RunShellShareLinkCopyAsync_CopiesShareLinkAndShowsNotificationWithoutPrintingLocalPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            Uri shareLink = new("https://cloud.example/s/generated-token");
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Docs/report.pdf",
                Kind = SyncEntryKind.File,
                RemoteNodeId = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                SyncedAtUtc = new DateTime(2026, 06, 20, 12, 00, 00, DateTimeKind.Utc),
            });
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--copy-shell-share-link", selectedPath]);
            using StringWriter output = new();
            FakeDesktopClipboardService clipboard = new();
            FakeDesktopNotificationService notifications = new();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                paths,
                options,
                output,
                shareLinkClient: new FakeDesktopShellShareLinkClient(
                    DesktopShellShareLinkResult.Created(shareLink)),
                clipboardService: clipboard,
                notificationService: notifications);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(clipboard.CopiedText, Is.EqualTo(shareLink.AbsoluteUri));
                Assert.That(notifications.Messages, Has.Count.EqualTo(1));
                Assert.That(notifications.Messages[0].Title, Is.EqualTo("Cotton Sync"));
                Assert.That(notifications.Messages[0].Message, Is.EqualTo("Share link copied to clipboard."));
                Assert.That(report, Does.Contain("Cotton Sync Desktop copy share link"));
                Assert.That(report, Does.Contain("Status: resolved"));
                Assert.That(report, Does.Contain("ShareLinkApi: available"));
                Assert.That(report, Does.Contain("ShareLinkCreated: true"));
                Assert.That(report, Does.Contain("ShareLinkCopied: true"));
                Assert.That(report, Does.Contain("Result: passed"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("report.pdf"));
                Assert.That(report, Does.Not.Contain("ShareLink:"));
            });
        }

        [Test]
        public async Task RunShellShareLinkCopyAsync_RejectsLocalOnlyPathWithoutCopying()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "local-only.txt");
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            await pairStore.UpsertAsync(CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot));
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--copy-shell-share-link", selectedPath]);
            using StringWriter output = new();
            FakeDesktopClipboardService clipboard = new();
            FakeDesktopNotificationService notifications = new();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                paths,
                options,
                output,
                clipboardService: clipboard,
                notificationService: notifications);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(clipboard.CopiedText, Is.Null);
                Assert.That(notifications.Messages, Has.Count.EqualTo(1));
                Assert.That(notifications.Messages[0].Title, Is.EqualTo("Cotton Sync"));
                Assert.That(notifications.Messages[0].Message, Is.EqualTo("This item is not synced yet."));
                Assert.That(report, Does.Contain("Status: missing-baseline"));
                Assert.That(report, Does.Contain("ShareLinkApi: unavailable"));
                Assert.That(report, Does.Contain("ShareLinkCreated: false"));
                Assert.That(report, Does.Contain("ShareLinkCopied: false"));
                Assert.That(report, Does.Contain("FailureReason: target-missing-baseline"));
                Assert.That(report, Does.Contain("Result: failed"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("local-only.txt"));
            });
        }

        [TestCase("auth-token-missing")]
        [TestCase("auth-refresh-failed")]
        public async Task RunShellShareLinkCopyAsync_ShowsSignInMessageWhenAuthIsUnavailable(
            string failureReason)
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Docs/report.pdf",
                Kind = SyncEntryKind.File,
                RemoteNodeId = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                SyncedAtUtc = new DateTime(2026, 06, 20, 12, 00, 00, DateTimeKind.Utc),
            });
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--copy-shell-share-link", selectedPath]);
            using StringWriter output = new();
            FakeDesktopClipboardService clipboard = new();
            FakeDesktopNotificationService notifications = new();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                paths,
                options,
                output,
                shareLinkClient: new FakeDesktopShellShareLinkClient(
                    DesktopShellShareLinkResult.Failed(failureReason)),
                clipboardService: clipboard,
                notificationService: notifications);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(clipboard.CopiedText, Is.Null);
                Assert.That(notifications.Messages, Has.Count.EqualTo(1));
                Assert.That(notifications.Messages[0].Message, Is.EqualTo("Sign in to Cotton Sync and try again."));
                Assert.That(report, Does.Contain("Status: resolved"));
                Assert.That(report, Does.Contain("ShareLinkCopied: false"));
                Assert.That(report, Does.Contain("FailureReason: " + failureReason));
                Assert.That(report, Does.Contain("Result: failed"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("report.pdf"));
            });
        }

        [Test]
        public async Task RunShellShareLinkCopyAsync_RejectsExplicitServerDifferentFromStoredSession()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = new Uri("https://account.example.test/"),
            });
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Docs/report.pdf",
                Kind = SyncEntryKind.File,
                RemoteNodeId = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                SyncedAtUtc = DateTime.UtcNow,
            });
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
            [
                "--data-dir",
                paths.DataDirectory,
                "--server-url",
                "https://unrelated.example.test/",
                "--copy-shell-share-link",
                selectedPath,
            ]);
            using StringWriter output = new();
            FakeDesktopClipboardService clipboard = new();
            FakeDesktopNotificationService notifications = new();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                paths,
                options,
                output,
                clipboardService: clipboard,
                notificationService: notifications);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(clipboard.CopiedText, Is.Null);
                Assert.That(notifications.Messages.Single().Message, Is.EqualTo("Sign in to Cotton Sync and try again."));
                Assert.That(output.ToString(), Does.Contain("FailureReason: server-url-session-mismatch"));
            });
        }

        [Test]
        public async Task RunShellShareLinkSmokeAsync_RequiresExplicitDataDirectory()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--shell-share-link-smoke"]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(report, Does.Contain("--data-dir"));
                Assert.That(report, Does.Contain("real user profile"));
            });
        }

        [Test]
        public async Task RunShellShareLinkSmokeAsync_CoversCopyAndFailureCases()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--shell-share-link-smoke",
                    "--data-dir",
                    _tempDirectory,
                    "--server-url",
                    "https://share-link-smoke.example.test/",
                ]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkSmokeAsync(paths, options, output);
            int repeatExitCode = await DesktopCommandLineRunner.RunShellShareLinkSmokeAsync(paths, options, output);
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            AppPreferences preferences = await preferencesStore.GetAsync();
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            IReadOnlyList<SyncPairSettings> syncPairs = await pairStore.ListAsync();

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(repeatExitCode, Is.EqualTo(0));
                Assert.That(syncPairs, Has.Count.EqualTo(1));
                Assert.That(report, Does.Contain("PASS: State-backed file share link copied"));
                Assert.That(report, Does.Contain("PASS: State-backed remote-only placeholder share link copied"));
                Assert.That(report, Does.Contain("PASS: State-backed hydrated placeholder share link copied"));
                Assert.That(report, Does.Contain("PASS: State-backed folder share link copied"));
                Assert.That(report, Does.Contain("PASS: Local-only item is rejected without clipboard write"));
                Assert.That(report, Does.Contain("PASS: Signed-out share link target asks for sign-in"));
                Assert.That(report, Does.Contain("Failures: 0"));
                Assert.That(report, Does.Contain("Result: passed"));
                Assert.That(report, Does.Not.Contain(_tempDirectory));
                Assert.That(report, Does.Not.Contain("synced-file.txt"));
                Assert.That(report, Does.Not.Contain("local-only.txt"));
                Assert.That(
                    preferences.RememberedServerUrl,
                    Is.EqualTo(new Uri("https://share-link-smoke.example.test/")));
            });
        }

        [Test]
        public async Task RunShellShareLinkTargetAsync_ReturnsFailureForLocalOnlyPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "local-only.txt");
            SqliteSyncPairSettingsStore pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            await pairStore.UpsertAsync(CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot));
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--resolve-shell-share-link-target", selectedPath]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkTargetAsync(paths, options, output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(report, Does.Contain("Status: missing-baseline"));
                Assert.That(report, Does.Contain("TargetResolved: false"));
                Assert.That(report, Does.Contain("TargetHasRemoteIdentity: false"));
                Assert.That(report, Does.Contain("ShareLinkApi: unavailable"));
                Assert.That(report, Does.Contain("CanCreateShareLink: false"));
                Assert.That(report, Does.Contain("ShareLinkCreated: false"));
                Assert.That(report, Does.Contain("TargetKind: unknown"));
                Assert.That(report, Does.Contain("Result: failed"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("local-only.txt"));
            });
        }

        [Test]
        public async Task RunShellShareLinkTargetAsync_FailsResolvedTargetWhenServerUrlIsMissing()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            SqliteSyncPairSettingsStore pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Docs/report.pdf",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
                SyncedAtUtc = new DateTime(2026, 06, 20, 12, 00, 00, DateTimeKind.Utc),
            });
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--resolve-shell-share-link-target", selectedPath]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkTargetAsync(paths, options, output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(report, Does.Contain("Status: resolved"));
                Assert.That(report, Does.Contain("TargetHasRemoteIdentity: true"));
                Assert.That(report, Does.Contain("ShareLinkApi: unavailable"));
                Assert.That(report, Does.Contain("CanCreateShareLink: false"));
                Assert.That(report, Does.Contain("ShareLinkCreated: false"));
                Assert.That(report, Does.Contain("FailureReason: server-url-missing"));
                Assert.That(report, Does.Contain("Result: failed"));
                Assert.That(report, Does.Not.Contain(localRoot));
                Assert.That(report, Does.Not.Contain("report.pdf"));
            });
        }
    }
}
