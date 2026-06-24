// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class DesktopCommandLineRunnerTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-desktop-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public async Task RunSelfTestAsync_PrintsReportAndReturnsPlatformSecurityResult()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory]);
            using var output = new StringWriter();
            bool tokenStorageIsReleaseSecure = DesktopTokenStorageCapabilities.CreateSnapshot().IsReleaseSecure;

            int exitCode = await DesktopCommandLineRunner.RunSelfTestAsync(options, output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(report, Does.Contain("Cotton Sync Desktop self-test"));
                Assert.That(report, Does.Contain("[OK] Preferences database"));
                Assert.That(report, Does.Contain("[SKIP] Desktop sync change feed"));
                Assert.That(report, Does.Contain(tokenStorageIsReleaseSecure ? "[OK] Token storage" : "[FAIL] Token storage"));
                Assert.That(exitCode, Is.EqualTo(tokenStorageIsReleaseSecure ? 0 : 1));
                Assert.That(report, Does.Contain(tokenStorageIsReleaseSecure ? "Result: passed" : "Result: failed"));
            });
        }

        [Test]
        public async Task RunExportDiagnosticsAsync_PrintsBundlePathAndCreatesArchive()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunExportDiagnosticsAsync(options, output);

            string report = output.ToString();
            string bundlePrefix = "Bundle: ";
            string bundlePath = report
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith(bundlePrefix, StringComparison.Ordinal))[bundlePrefix.Length..];
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(report, Does.Contain("Cotton Sync Desktop diagnostics"));
                Assert.That(report, Does.Contain("Mode: public"));
                Assert.That(File.Exists(bundlePath), Is.True);
                Assert.That(Path.GetDirectoryName(bundlePath), Is.EqualTo(Path.Combine(_tempDirectory, "diagnostics")));
            });
        }

        [Test]
        public async Task RunExportDiagnosticsAsync_UsesPrivateSupportModeOnlyWhenExplicitlyRequested()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--export-diagnostics-private"]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunExportDiagnosticsAsync(options, output);

            string report = output.ToString();
            string bundlePrefix = "Bundle: ";
            string bundlePath = report
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith(bundlePrefix, StringComparison.Ordinal))[bundlePrefix.Length..];
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(report, Does.Contain("Mode: private-support"));
                Assert.That(Path.GetFileName(bundlePath), Does.Contain("private-support"));
                Assert.That(File.Exists(bundlePath), Is.True);
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_UnregistersOnlyVirtualFilesPairs()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings fullMirror = CreateSyncPair("Full", SyncPairMode.FullMirror, Path.Combine(_tempDirectory, "full"));
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(fullMirror);
            await store.UpsertAsync(virtualFiles);
            var adapter = new FakeCloudFilesAdapter();
            var storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);
            IReadOnlyList<SyncPairSettings> remainingPairs = await store.ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(remainingPairs.Select(static pair => pair.Id), Is.EquivalentTo(new[] { fullMirror.Id, virtualFiles.Id }));
                Assert.That(output.ToString(), Does.Contain("Roots cleaned: 1"));
                Assert.That(output.ToString(), Does.Contain("Orphaned storage-provider roots cleaned."));
                Assert.That(output.ToString(), Does.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenUnregisterFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(virtualFiles);
            var adapter = new FakeCloudFilesAdapter
            {
                Exception = new InvalidOperationException("unregister failed"),
            };
            var storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenOrphanedStorageProviderCleanupFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            var adapter = new FakeCloudFilesAdapter();
            var storageProvider = new FakeStorageProviderSyncRootRegistrar
            {
                Exception = new InvalidOperationException("orphan cleanup failed"),
            };
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failed orphaned storage-provider cleanup"));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        public async Task RunShellShareLinkTargetAsync_CreatesShareLinkForSyncedFileWithoutPrintingLocalPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "Docs", "report.pdf");
            var shareLink = new Uri("https://cloud.example/s/generated-token");
            var pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
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
            using var output = new StringWriter();

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
                ]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkSmokeAsync(paths, options, output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
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
            });
        }

        [Test]
        public async Task RunShellShareLinkTargetAsync_ReturnsFailureForLocalOnlyPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state"));
            string localRoot = Path.Combine(_tempDirectory, "cloud");
            string selectedPath = Path.Combine(localRoot, "local-only.txt");
            var pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            await pairStore.UpsertAsync(CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot));
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", paths.DataDirectory, "--resolve-shell-share-link-target", selectedPath]);
            using var output = new StringWriter();

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
            var pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair("Cloud", SyncPairMode.WindowsVirtualFiles, localRoot);
            await pairStore.UpsertAsync(syncPair);
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
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
            using var output = new StringWriter();

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

        [Test]
        [Platform(Include = "Win")]
        public async Task RunWindowsVirtualFilesSmokeAsync_RejectsRootOutsideIsolatedQaDrive()
        {
            string unsafeRoot = Path.Combine(_tempDirectory, "vfs-root");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--data-dir",
                    Path.Combine(_tempDirectory, "state"),
                    "--local-root",
                    unsafeRoot,
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunWindowsVirtualFilesSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state")),
                options,
                output,
                new FakeCloudFilesAdapter());

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain(@"refuses to touch paths outside S:\CottonSyncVfsQa\..."));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeAsync_RequiresExplicitDataDirectory()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--server",
                    "app.cottoncloud.dev",
                    "--local-root",
                    Path.Combine(_tempDirectory, "client-a"),
                    "--second-local-root",
                    Path.Combine(_tempDirectory, "client-b"),
                    "--remote-path",
                    "/CottonSyncQa/DesktopSmoke",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--data-dir"));
                Assert.That(output.ToString(), Does.Contain("real user profile"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeAsync_RejectsNonEmptyLocalRoots()
        {
            string dataDirectory = Path.Combine(_tempDirectory, "smoke-state");
            string firstLocalRoot = Path.Combine(_tempDirectory, "client-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "client-b");
            Directory.CreateDirectory(firstLocalRoot);
            Directory.CreateDirectory(secondLocalRoot);
            await File.WriteAllTextAsync(Path.Combine(firstLocalRoot, "existing.txt"), "do not touch");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--server",
                    "app.cottoncloud.dev",
                    "--data-dir",
                    dataDirectory,
                    "--local-root",
                    firstLocalRoot,
                    "--second-local-root",
                    secondLocalRoot,
                    "--remote-path",
                    "/CottonSyncQa/DesktopSmoke",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--local-root must be empty or missing"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeAsync_RejectsNonEmptyLocalRootsWhenPreserveExistingLocalFilesIsEnabled()
        {
            string dataDirectory = Path.Combine(_tempDirectory, "smoke-state");
            string firstLocalRoot = Path.Combine(_tempDirectory, "client-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "client-b");
            Directory.CreateDirectory(firstLocalRoot);
            Directory.CreateDirectory(secondLocalRoot);
            await File.WriteAllTextAsync(Path.Combine(firstLocalRoot, "existing.txt"), "do not touch");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--server",
                    "app.cottoncloud.dev",
                    "--data-dir",
                    dataDirectory,
                    "--local-root",
                    firstLocalRoot,
                    "--second-local-root",
                    secondLocalRoot,
                    "--remote-path",
                    "/CottonSyncQa/DesktopSmoke",
                    "--live-sync-smoke-preserve-existing-local-files",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--local-root must be empty or missing"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeAsync_RejectsInvalidSyncModeBeforeTouchingRoots()
        {
            string dataDirectory = Path.Combine(_tempDirectory, "smoke-state");
            string firstLocalRoot = Path.Combine(_tempDirectory, "client-a");
            string secondLocalRoot = Path.Combine(_tempDirectory, "client-b");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--server",
                    "app.cottoncloud.dev",
                    "--data-dir",
                    dataDirectory,
                    "--local-root",
                    firstLocalRoot,
                    "--second-local-root",
                    secondLocalRoot,
                    "--remote-path",
                    "/CottonSyncQa/DesktopSmoke",
                    "--sync-mode",
                    "placeholder",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("Unsupported sync mode"));
                Assert.That(Directory.Exists(firstLocalRoot), Is.False);
                Assert.That(Directory.Exists(secondLocalRoot), Is.False);
            });
        }

        [Test]
        public async Task RunUpdateDiscoverySmokeAsync_RequiresExplicitDataDirectory()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-discovery-smoke",
                    "--update-manifest-url",
                    "https://updates.example/release-manifest.json",
                ]);
            var updateService = new FakeDesktopUpdateService(DesktopAppPaths.CreateForDataDirectory(_tempDirectory));
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunUpdateDiscoverySmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                updateService);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--data-dir"));
                Assert.That(output.ToString(), Does.Contain("real user profile"));
                Assert.That(updateService.CheckCalls, Is.EqualTo(0));
                Assert.That(updateService.DownloadCalls, Is.EqualTo(0));
            });
        }

        [Test]
        public async Task RunUpdateDiscoverySmokeAsync_DownloadsUpdateAndExportsDiagnostics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-discovery-smoke",
                    "--data-dir",
                    _tempDirectory,
                    "--update-manifest-url",
                    "https://updates.example/release-manifest.json",
                    "--expected-update-version",
                    "0.1.1",
                ]);
            var updateService = new FakeDesktopUpdateService(paths);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunUpdateDiscoverySmokeAsync(
                paths,
                options,
                output,
                updateService);
            DesktopPendingUpdate? pendingUpdate = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory).TryLoad();

            string report = output.ToString();
            string bundlePrefix = "Bundle: ";
            string bundlePath = report
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith(bundlePrefix, StringComparison.Ordinal))[bundlePrefix.Length..];

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(updateService.CheckCalls, Is.EqualTo(1));
                Assert.That(updateService.DownloadCalls, Is.EqualTo(1));
                Assert.That(report, Does.Contain("PASS: Installed version discovers a newer release"));
                Assert.That(report, Does.Contain("PASS: Update installer is downloaded into cache"));
                Assert.That(report, Does.Contain("PASS: Pending update metadata is persisted"));
                Assert.That(report, Does.Contain("PASS: Diagnostics bundle records update status"));
                Assert.That(pendingUpdate?.Version, Is.EqualTo("0.1.1"));
                Assert.That(pendingUpdate?.SizeBytes, Is.EqualTo(FakeDesktopUpdateService.InstallerSizeBytes));
                Assert.That(File.Exists(pendingUpdate?.InstallerPath), Is.True);
                Assert.That(File.Exists(bundlePath), Is.True);
            });
        }

        [Test]
        public async Task RunUpdateInstallSmokeAsync_RequiresExplicitDataDirectory()
        {
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.cmd");
            File.WriteAllText(installerPath, "exit /b 0");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-install-smoke",
                    "--update-installer-path",
                    installerPath,
                ]);
            var installer = new FakeDesktopUpdateInstaller();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunUpdateInstallSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                installer);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--data-dir"));
                Assert.That(output.ToString(), Does.Contain("real user profile"));
                Assert.That(installer.Calls, Is.Zero);
            });
        }

        [Test]
        public async Task RunUpdateInstallSmokeAsync_LaunchesInstallerAndExportsDiagnostics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.cmd");
            File.WriteAllText(installerPath, "exit /b 0");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-install-smoke",
                    "--data-dir",
                    _tempDirectory,
                    "--update-installer-path",
                    installerPath,
                ]);
            var installer = new FakeDesktopUpdateInstaller();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunUpdateInstallSmokeAsync(
                paths,
                options,
                output,
                installer);

            string report = output.ToString();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(installer.Calls, Is.EqualTo(1));
                Assert.That(installer.InstallerPath, Is.EqualTo(installerPath));
                Assert.That(installer.LaunchAfterUpdate, Is.True);
                Assert.That(report, Does.Contain("PASS: Update installer launch returns a process id"));
                Assert.That(report, Does.Contain("PASS: Update installer startup probe does not fail"));
                Assert.That(report, Does.Contain("PASS: Diagnostics bundle records installer launch outcome"));
                Assert.That(report, Does.Contain("PASS: Installer launch wrote a trace log"));
                Assert.That(report, Does.Contain("Result: passed"));
            });
        }

        private static SyncPairSettings CreateSyncPair(string displayName, SyncPairMode mode, string localRootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                LocalRootPath = localRootPath,
                RemoteDisplayPath = "/" + displayName,
                RemoteRootNodeId = Guid.NewGuid(),
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private sealed class FakeDesktopUpdateService : IDesktopUpdateService
        {
            public const long InstallerSizeBytes = 9;
            private const string LatestVersion = "0.1.1";
            private const string InstallerName = "CottonSync-Windows-Setup.exe";
            private static readonly string InstallerSha256 = new('a', 64);
            private readonly DesktopAppPaths _paths;
            private readonly DesktopReleaseManifest _manifest;
            private readonly DesktopReleaseAsset _installerAsset;

            public FakeDesktopUpdateService(DesktopAppPaths paths)
            {
                _paths = paths;
                _installerAsset = new DesktopReleaseAsset(
                    InstallerName,
                    InstallerSha256,
                    InstallerSizeBytes,
                    new Uri("https://updates.example/" + InstallerName));
                _manifest = new DesktopReleaseManifest(
                    1,
                    "Cotton Sync",
                    LatestVersion,
                    "v" + LatestVersion,
                    "0000000000000000000000000000000000000000",
                    "main",
                    new Uri("https://updates.example/releases/v" + LatestVersion),
                    [_installerAsset]);
            }

            public int CheckCalls { get; private set; }

            public int DownloadCalls { get; private set; }

            public Task<DesktopUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
            {
                CheckCalls++;
                return Task.FromResult(new DesktopUpdateCheckResult(
                    _manifest,
                    DesktopSemanticVersion.Parse("0.1.0"),
                    DesktopSemanticVersion.Parse(LatestVersion),
                    IsUpdateAvailable: true,
                    _installerAsset));
            }

            public async Task<DesktopUpdateDownloadResult> DownloadInstallerAsync(
                DesktopUpdateCheckResult checkResult,
                IProgress<DesktopUpdateDownloadProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadCalls++;
                string versionDirectory = Path.Combine(_paths.UpdateCacheDirectory, LatestVersion);
                Directory.CreateDirectory(versionDirectory);
                string installerPath = Path.Combine(versionDirectory, InstallerName);
                await File.WriteAllTextAsync(installerPath, "installer", cancellationToken).ConfigureAwait(false);
                return new DesktopUpdateDownloadResult(
                    checkResult.Manifest,
                    _installerAsset,
                    installerPath,
                    InstallerSha256,
                    InstallerSizeBytes);
            }
        }

        private sealed class FakeDesktopShellShareLinkClient : IDesktopShellShareLinkClient
        {
            private readonly DesktopShellShareLinkResult _result;

            public FakeDesktopShellShareLinkClient(DesktopShellShareLinkResult result)
            {
                _result = result;
            }

            public Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
                ShellShareLinkTarget target,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }
        }

        private class FakeDesktopClipboardService : IDesktopClipboardService
        {
            public string? CopiedText { get; private set; }

            public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
            {
                CopiedText = text;
                return Task.CompletedTask;
            }
        }

        private class FakeDesktopNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => true;

            public List<(string Title, string Message)> Messages { get; } = [];

            public void Show(string title, string message)
            {
                Messages.Add((title, message));
            }
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<SyncPairSettings> UnregisteredPairs { get; } = [];

            public Exception? Exception { get; set; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                UnregisteredPairs.Add(syncPair);
                if (Exception is not null)
                {
                    throw Exception;
                }
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            public int UnregisterAllCalls { get; private set; }

            public Exception? Exception { get; set; }

            public bool IsSupported()
            {
                return true;
            }

            public bool IsRegistered(Guid syncPairId)
            {
                throw new NotSupportedException();
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void Unregister(Guid syncPairId)
            {
                throw new NotSupportedException();
            }

            public void UnregisterAllForCurrentUser()
            {
                UnregisterAllCalls++;
                if (Exception is not null)
                {
                    throw Exception;
                }
            }
        }

        private sealed class FakeDesktopUpdateInstaller : IDesktopUpdateInstaller
        {
            public int Calls { get; private set; }

            public string? InstallerPath { get; private set; }

            public bool? LaunchAfterUpdate { get; private set; }

            public DesktopUpdateInstallResult StartSilentInstall(
                string installerPath,
                bool launchAfterUpdate)
            {
                Calls++;
                InstallerPath = installerPath;
                LaunchAfterUpdate = launchAfterUpdate;
                return new DesktopUpdateInstallResult(42, true, 0);
            }
        }
    }
}
