// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public class DesktopDiagnosticsExporterTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-diagnostics-" + Guid.NewGuid().ToString("N"));
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
        public async Task ExportAsync_CreatesArchiveWithDiagnosticsJsonAndLogs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            File.WriteAllText(paths.LogFilePath, "sync log");
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            Assert.Multiple(() =>
            {
                Assert.That(archive.GetEntry("diagnostics.json"), Is.Not.Null);
                Assert.That(archive.GetEntry("logs/cotton-sync.log"), Is.Not.Null);
            });
        }

        [Test]
        public async Task ExportAsync_DoesNotIncludeTokenStoreOrDatabases()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            File.WriteAllText(paths.TokenStorePath, "secret-token");
            File.WriteAllText(paths.AppDatabasePath, "app-db");
            File.WriteAllText(paths.SyncStateDatabasePath, "sync-db");
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string[] entryNames = archive.Entries.Select(static entry => entry.FullName).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(entryNames, Does.Not.Contain("tokens.json"));
                Assert.That(entryNames, Does.Not.Contain("sync-app.db"));
                Assert.That(entryNames, Does.Not.Contain("sync-state.db"));
            });
        }

        [Test]
        public async Task ExportAsync_RedactsSecretsFromLogs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            File.WriteAllText(
                paths.LogFilePath,
                """
                Authorization: Bearer access-token
                {"password":"secret","refreshToken":"refresh-token","totpCode":"123456","twoFactorCode":"654321"}
                https://app.cottoncloud.dev/callback?access_token=query-access&refresh_token=query-refresh
                """);
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string logContent = ReadEntry(archive, "logs/cotton-sync.log");
            Assert.Multiple(() =>
            {
                Assert.That(logContent, Does.Contain("Bearer [redacted]"));
                Assert.That(logContent, Does.Contain("""password":"[redacted]"""));
                Assert.That(logContent, Does.Contain("""refreshToken":"[redacted]"""));
                Assert.That(logContent, Does.Contain("""totpCode":"[redacted]"""));
                Assert.That(logContent, Does.Contain("""twoFactorCode":"[redacted]"""));
                Assert.That(logContent, Does.Contain("access_token=[redacted]&"));
                Assert.That(logContent, Does.Contain("refresh_token=[redacted]"));
                Assert.That(logContent, Does.Not.Contain("access-token"));
                Assert.That(logContent, Does.Not.Contain("refresh-token"));
                Assert.That(logContent, Does.Not.Contain("query-access"));
                Assert.That(logContent, Does.Not.Contain("query-refresh"));
                Assert.That(logContent, Does.Not.Contain("secret"));
                Assert.That(logContent, Does.Not.Contain("123456"));
                Assert.That(logContent, Does.Not.Contain("654321"));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesSyncPairModeAsReadableString()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            string? mode = document.RootElement
                .GetProperty("syncPairs")[0]
                .GetProperty("mode")
                .GetString();

            Assert.That(mode, Is.EqualTo("fullMirror"));
        }

        [Test]
        public async Task ExportAsync_SerializesDataPathMetadata()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement dataPaths = document.RootElement.GetProperty("dataPaths");

            Assert.Multiple(() =>
            {
                Assert.That(dataPaths.GetProperty("dataDirectory").GetString(), Is.EqualTo("[data-directory]"));
                Assert.That(dataPaths.GetProperty("appDatabasePath").GetString(), Is.EqualTo("[app-database]"));
                Assert.That(dataPaths.GetProperty("syncStateDatabasePath").GetString(), Is.EqualTo("[sync-state-database]"));
                Assert.That(dataPaths.GetProperty("tokenStorePath").GetString(), Is.EqualTo("[token-store]"));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesCloudFilesDiagnosticEvents()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            var cloudFilesEvent = new WindowsCloudFilesDiagnosticEvent(
                DateTimeOffset.Parse("2026-06-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                "hydrate",
                "failed",
                "11111111-1111-1111-1111-111111111111",
                @"S:\CottonSync",
                "remote-only.txt",
                "Downloaded cloud-file content hash does not match the placeholder identity.",
                unchecked((int)0x8007017C));

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths, [cloudFilesEvent]));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement item = document.RootElement.GetProperty("cloudFilesEvents")[0];

            Assert.Multiple(() =>
            {
                Assert.That(item.GetProperty("operation").GetString(), Is.EqualTo("hydrate"));
                Assert.That(item.GetProperty("status").GetString(), Is.EqualTo("failed"));
                Assert.That(item.GetProperty("syncPairId").GetString(), Is.EqualTo("[sync-pair-id]"));
                Assert.That(item.GetProperty("localRootPath").GetString(), Is.EqualTo("[cloud-files-local-root]"));
                Assert.That(item.GetProperty("relativePath").GetString(), Is.EqualTo("[cloud-files-relative-path]"));
                Assert.That(item.GetProperty("hResult").GetInt32(), Is.EqualTo(unchecked((int)0x8007017C)));
            });
        }

        [Test]
        public async Task ExportAsync_RemovesAccountServerAndPathValuesFromPublicBundleAndLogs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            const string serverUrl = "https://private.cotton.example/";
            const string accountName = "person@example.test";
            const string localRoot = @"C:\Users\Person\Cotton\Sensitive";
            const string remoteRoot = "/Private/Sensitive";
            const string cloudRelativePath = "Private/file-name.txt";
            string logContent =
                serverUrl
                + Environment.NewLine
                + accountName
                + Environment.NewLine
                + paths.DataDirectory
                + Environment.NewLine
                + localRoot
                + Environment.NewLine
                + remoteRoot
                + Environment.NewLine
                + cloudRelativePath;
            File.WriteAllText(paths.LogFilePath, logContent);
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(
                    paths,
                    [
                        new WindowsCloudFilesDiagnosticEvent(
                            DateTimeOffset.Parse("2026-06-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                            "create-placeholder",
                            "failed",
                            "11111111-1111-1111-1111-111111111111",
                            localRoot,
                            cloudRelativePath,
                            "Failed under " + localRoot + " for " + cloudRelativePath,
                            unchecked((int)0x8007017C)),
                    ],
                    serverUrl: serverUrl,
                    accountName: accountName,
                    localPath: localRoot,
                    remotePath: remoteRoot));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            string exportedLog = ReadEntry(archive, "logs/cotton-sync.log");

            Assert.Multiple(() =>
            {
                Assert.That(diagnosticsJson, Does.Not.Contain(serverUrl));
                Assert.That(diagnosticsJson, Does.Not.Contain(accountName));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.DataDirectory));
                Assert.That(diagnosticsJson, Does.Not.Contain(localRoot));
                Assert.That(diagnosticsJson, Does.Not.Contain(remoteRoot));
                Assert.That(diagnosticsJson, Does.Not.Contain(cloudRelativePath));
                Assert.That(diagnosticsJson, Does.Contain("[server-url]"));
                Assert.That(diagnosticsJson, Does.Contain("[sync-pair-1-local-root]"));
                Assert.That(exportedLog, Does.Not.Contain(serverUrl));
                Assert.That(exportedLog, Does.Not.Contain(accountName));
                Assert.That(exportedLog, Does.Not.Contain(paths.DataDirectory));
                Assert.That(exportedLog, Does.Not.Contain(localRoot));
                Assert.That(exportedLog, Does.Not.Contain(remoteRoot));
                Assert.That(exportedLog, Does.Not.Contain(cloudRelativePath));
                Assert.That(exportedLog, Does.Contain("[server-url]"));
                Assert.That(exportedLog, Does.Contain("[account]"));
                Assert.That(exportedLog, Does.Contain("[data-directory]"));
            });
        }

        [Test]
        public async Task ExportAsync_PrivateSupportModeKeepsSupportContextAndStillRedactsSecrets()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            const string serverUrl = "https://private.cotton.example/";
            const string accountName = "person@example.test";
            const string localRoot = @"C:\Users\Person\Cotton\Sensitive";
            const string remoteRoot = "/Private/Sensitive";
            const string cloudRelativePath = "Private/file-name.txt";
            File.WriteAllText(
                paths.LogFilePath,
                string.Join(
                    Environment.NewLine,
                    serverUrl,
                    accountName,
                    paths.DataDirectory,
                    localRoot,
                    remoteRoot,
                    cloudRelativePath,
                    "Authorization: Bearer access-token"));
            var exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(
                    paths,
                    [
                        new WindowsCloudFilesDiagnosticEvent(
                            DateTimeOffset.Parse("2026-06-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                            "create-placeholder",
                            "failed",
                            "11111111-1111-1111-1111-111111111111",
                            localRoot,
                            cloudRelativePath,
                            "Failed under " + localRoot + " for " + cloudRelativePath,
                            unchecked((int)0x8007017C)),
                    ],
                    serverUrl: serverUrl,
                    accountName: accountName,
                    localPath: localRoot,
                    remotePath: remoteRoot),
                DesktopDiagnosticsExportOptions.PrivateSupport);

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            string exportedLog = ReadEntry(archive, "logs/cotton-sync.log");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement root = document.RootElement;
            JsonElement syncPair = root.GetProperty("syncPairs")[0];
            JsonElement cloudFilesEvent = root.GetProperty("cloudFilesEvents")[0];

            Assert.Multiple(() =>
            {
                Assert.That(Path.GetFileName(archivePath), Does.Contain("private-support"));
                Assert.That(root.GetProperty("serverUrl").GetString(), Is.EqualTo(serverUrl));
                Assert.That(root.GetProperty("accountName").GetString(), Is.EqualTo(accountName));
                Assert.That(root.GetProperty("dataPaths").GetProperty("dataDirectory").GetString(), Is.EqualTo(paths.DataDirectory));
                Assert.That(syncPair.GetProperty("localPath").GetString(), Is.EqualTo(localRoot));
                Assert.That(syncPair.GetProperty("remotePath").GetString(), Is.EqualTo(remoteRoot));
                Assert.That(cloudFilesEvent.GetProperty("localRootPath").GetString(), Is.EqualTo(localRoot));
                Assert.That(cloudFilesEvent.GetProperty("relativePath").GetString(), Is.EqualTo(cloudRelativePath));
                Assert.That(exportedLog, Does.Contain(serverUrl));
                Assert.That(exportedLog, Does.Contain(accountName));
                Assert.That(exportedLog, Does.Contain(paths.DataDirectory));
                Assert.That(exportedLog, Does.Contain(localRoot));
                Assert.That(exportedLog, Does.Contain(remoteRoot));
                Assert.That(exportedLog, Does.Contain(cloudRelativePath));
                Assert.That(exportedLog, Does.Contain("Bearer [redacted]"));
                Assert.That(exportedLog, Does.Not.Contain("access-token"));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesStateAndRuntimeHealthMetrics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            var syncState = new SyncStateStoreDiagnostics(
                FileSizeBytes: 8192,
                PageCount: 2,
                FreelistCount: 1,
                PageSizeBytes: 4096,
                SyncEntryCount: 3,
                SyncChangeCursorCount: 1);
            var runtimeHealth = new DesktopRuntimeHealthSnapshot(
                ProcessId: 123,
                ProcessName: "Cotton.Sync.Desktop",
                WorkingSetBytes: 456,
                PrivateMemoryBytes: 321,
                ThreadCount: 7,
                HandleCount: 9);

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(paths, syncState: syncState, runtimeHealth: runtimeHealth));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement state = document.RootElement.GetProperty("syncState");
            JsonElement runtime = document.RootElement.GetProperty("runtimeHealth");

            Assert.Multiple(() =>
            {
                Assert.That(state.GetProperty("fileSizeBytes").GetInt64(), Is.EqualTo(8192));
                Assert.That(state.GetProperty("usedBytes").GetInt64(), Is.EqualTo(4096));
                Assert.That(state.GetProperty("freelistBytes").GetInt64(), Is.EqualTo(4096));
                Assert.That(state.GetProperty("syncEntryCount").GetInt64(), Is.EqualTo(3));
                Assert.That(state.GetProperty("syncChangeCursorCount").GetInt64(), Is.EqualTo(1));
                Assert.That(runtime.GetProperty("processId").GetInt32(), Is.EqualTo(123));
                Assert.That(runtime.GetProperty("workingSetBytes").GetInt64(), Is.EqualTo(456));
                Assert.That(runtime.GetProperty("privateMemoryBytes").GetInt64(), Is.EqualTo(321));
                Assert.That(runtime.GetProperty("threadCount").GetInt32(), Is.EqualTo(7));
                Assert.That(runtime.GetProperty("handleCount").GetInt32(), Is.EqualTo(9));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesSyncLifecycleDiagnostics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            var syncLifecycle = new DesktopSyncLifecycleDiagnosticsSnapshot(
                IsSignedIn: true,
                SyncCoreState: "running",
                IsBackgroundActive: true,
                SyncPairCount: 0,
                EnabledSyncPairCount: 0,
                HasNoSyncPairs: true,
                IsZeroPairBackgroundActive: true,
                Status: "zeroPairBackgroundActive",
                Details: "Signed in with no configured sync pairs; sync background is active.");

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(paths, syncLifecycle: syncLifecycle));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement lifecycle = document.RootElement.GetProperty("syncLifecycle");

            Assert.Multiple(() =>
            {
                Assert.That(lifecycle.GetProperty("isSignedIn").GetBoolean(), Is.True);
                Assert.That(lifecycle.GetProperty("syncCoreState").GetString(), Is.EqualTo("running"));
                Assert.That(lifecycle.GetProperty("isBackgroundActive").GetBoolean(), Is.True);
                Assert.That(lifecycle.GetProperty("syncPairCount").GetInt32(), Is.Zero);
                Assert.That(lifecycle.GetProperty("enabledSyncPairCount").GetInt32(), Is.Zero);
                Assert.That(lifecycle.GetProperty("hasNoSyncPairs").GetBoolean(), Is.True);
                Assert.That(lifecycle.GetProperty("isZeroPairBackgroundActive").GetBoolean(), Is.True);
                Assert.That(lifecycle.GetProperty("status").GetString(), Is.EqualTo("zeroPairBackgroundActive"));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesNotificationDiagnosticsAndSanitizesSelfTestDetails()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            var notification = new DesktopNotificationDiagnosticsSnapshot(
                Platform: "Windows",
                AdapterName: "Windows toast",
                IsSupported: true,
                IsDeliveryExecutableAvailable: true,
                IsIconAvailable: true,
                AppName: "Cotton Sync",
                AppUserModelId: "Cotton.Sync.Desktop",
                IsInstalledAppIdentityVerified: false,
                IdentityStatus: "debug-identity-only",
                Details: "PowerShell toast delivery helper is available, but installed Start Menu AppUserModelID identity is not verified.");
            var selfTestItems = new[]
            {
                new DesktopSelfTestItemSnapshot(
                    "Notification adapter",
                    false,
                    "adapter: Windows toast; icon: " + paths.DataDirectory,
                    Skipped: true),
            };

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(paths, notification: notification, selfTestItems: selfTestItems));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement notificationJson = document.RootElement.GetProperty("notification");
            JsonElement selfTest = document.RootElement.GetProperty("selfTestItems")[0];

            Assert.Multiple(() =>
            {
                Assert.That(notificationJson.GetProperty("platform").GetString(), Is.EqualTo("Windows"));
                Assert.That(notificationJson.GetProperty("isDeliveryExecutableAvailable").GetBoolean(), Is.True);
                Assert.That(notificationJson.GetProperty("isInstalledAppIdentityVerified").GetBoolean(), Is.False);
                Assert.That(notificationJson.GetProperty("identityStatus").GetString(), Is.EqualTo("debug-identity-only"));
                Assert.That(notificationJson.GetProperty("details").GetString(), Does.Contain("AppUserModelID identity is not verified"));
                Assert.That(selfTest.GetProperty("details").GetString(), Does.Contain("[data-directory]"));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.DataDirectory));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesUpdateDiagnosticsWithoutInstallerPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            string installerPath = Path.Combine(paths.UpdateCacheDirectory, "0.0.2", "CottonSync-Windows-Setup.exe");
            var update = new DesktopUpdateDiagnosticsSnapshot(
                CurrentVersion: "0.0.1",
                IsUpdateCacheDirectoryPresent: true,
                HasPendingUpdate: true,
                PendingVersion: "0.0.2",
                PendingInstallerSizeBytes: 1024,
                LastCheckAtUtc: DateTimeOffset.Parse("2026-06-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                LastCheckStatus: "succeeded",
                LastCheckSource: "download",
                LatestVersion: "0.0.2",
                IsUpdateAvailable: true,
                HasInstallerAsset: true,
                IsInstallerReady: true,
                ReleaseUrl: new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2"),
                FailureType: "IOException",
                FailureMessage: "Update cache failed under " + paths.UpdateCacheDirectory);

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths, update: update));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement updateJson = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(updateJson.GetProperty("currentVersion").GetString(), Is.EqualTo("0.0.1"));
                Assert.That(updateJson.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("succeeded"));
                Assert.That(updateJson.GetProperty("lastCheckSource").GetString(), Is.EqualTo("download"));
                Assert.That(updateJson.GetProperty("latestVersion").GetString(), Is.EqualTo("0.0.2"));
                Assert.That(updateJson.GetProperty("isUpdateAvailable").GetBoolean(), Is.True);
                Assert.That(updateJson.GetProperty("hasPendingUpdate").GetBoolean(), Is.True);
                Assert.That(updateJson.GetProperty("pendingVersion").GetString(), Is.EqualTo("0.0.2"));
                Assert.That(updateJson.GetProperty("pendingInstallerSizeBytes").GetInt64(), Is.EqualTo(1024));
                Assert.That(updateJson.GetProperty("failureMessage").GetString(), Does.Contain("[update-cache]"));
                Assert.That(diagnosticsJson, Does.Not.Contain(installerPath));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.UpdateCacheDirectory));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesCloudFilesRegistrationDiagnosticsWithoutLocalPaths()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            const string localRoot = @"C:\Users\Person\Cotton\Virtual";
            var cloudFilesRegistration = new DesktopCloudFilesRegistrationDiagnosticsSnapshot(
                IsWindows: true,
                IsStorageProviderHelperAvailable: true,
                IsStorageProviderSupported: true,
                VirtualFilesSyncPairCount: 1,
                RegisteredSyncPairCount: 1,
                MissingSyncPairCount: 0,
                UnknownSyncPairCount: 0,
                [
                    new DesktopCloudFilesSyncPairRegistrationSnapshot(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "Private virtual root",
                        localRoot,
                        IsEnabled: true,
                        IsExpectedRegistered: true,
                        IsRegistered: true,
                        Status: "registered",
                        Details: "Registered at " + localRoot),
                ]);

            string archivePath = await exporter.ExportAsync(
                paths,
                CreateBundle(paths, cloudFilesRegistration: cloudFilesRegistration));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement registration = document.RootElement.GetProperty("cloudFilesRegistration");
            JsonElement pair = registration.GetProperty("syncPairs")[0];

            Assert.Multiple(() =>
            {
                Assert.That(registration.GetProperty("virtualFilesSyncPairCount").GetInt32(), Is.EqualTo(1));
                Assert.That(registration.GetProperty("registeredSyncPairCount").GetInt32(), Is.EqualTo(1));
                Assert.That(pair.GetProperty("syncPairId").GetString(), Is.EqualTo(Guid.Empty.ToString()));
                Assert.That(pair.GetProperty("displayName").GetString(), Is.EqualTo("[cloud-files-sync-pair-1-name]"));
                Assert.That(pair.GetProperty("localRootPath").GetString(), Is.EqualTo("[cloud-files-sync-pair-1-local-root]"));
                Assert.That(pair.GetProperty("status").GetString(), Is.EqualTo("registered"));
                Assert.That(pair.GetProperty("details").GetString(), Does.Contain("[cloud-files-sync-pair-1-local-root]"));
                Assert.That(diagnosticsJson, Does.Not.Contain(localRoot));
                Assert.That(diagnosticsJson, Does.Not.Contain("Private virtual root"));
                Assert.That(diagnosticsJson, Does.Not.Contain("11111111-1111-1111-1111-111111111111"));
            });
        }

        [Test]
        public async Task ExportAsync_RemainsBoundedAfterLargeCloudFilesDiagnosticStorm()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var exporter = new DesktopDiagnosticsExporter();
            var diagnostics = new WindowsCloudFilesDiagnostics();

            for (int index = 0; index < 10_000; index++)
            {
                diagnostics.Record(
                    "create-placeholder",
                    "failed",
                    "11111111-1111-1111-1111-111111111111",
                    @"S:\CottonSyncVfsQa\root",
                    "node_modules/package-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".js",
                    "Placeholder creation failed.",
                    unchecked((int)0x8007017C));
            }

            IReadOnlyList<WindowsCloudFilesDiagnosticEvent> events = diagnostics.Snapshot();
            Stopwatch stopwatch = Stopwatch.StartNew();
            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths, events));
            stopwatch.Stop();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement cloudFilesEvents = document.RootElement.GetProperty("cloudFilesEvents");

            Assert.Multiple(() =>
            {
                Assert.That(events, Has.Count.EqualTo(200));
                Assert.That(cloudFilesEvents.GetArrayLength(), Is.EqualTo(200));
                Assert.That(
                    cloudFilesEvents[0].GetProperty("relativePath").GetString(),
                    Is.EqualTo("[cloud-files-relative-path]"));
                Assert.That(
                    cloudFilesEvents[199].GetProperty("relativePath").GetString(),
                    Is.EqualTo("[cloud-files-relative-path]"));
                Assert.That(new FileInfo(archivePath).Length, Is.LessThan(512 * 1024));
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            });
        }

        private static DesktopDiagnosticsBundle CreateBundle(
            DesktopAppPaths paths,
            IReadOnlyList<WindowsCloudFilesDiagnosticEvent>? cloudFilesEvents = null,
            SyncStateStoreDiagnostics? syncState = null,
            DesktopRuntimeHealthSnapshot? runtimeHealth = null,
            DesktopSyncLifecycleDiagnosticsSnapshot? syncLifecycle = null,
            DesktopNotificationDiagnosticsSnapshot? notification = null,
            DesktopUpdateDiagnosticsSnapshot? update = null,
            DesktopCloudFilesRegistrationDiagnosticsSnapshot? cloudFilesRegistration = null,
            IReadOnlyList<DesktopSelfTestItemSnapshot>? selfTestItems = null,
            string serverUrl = "https://app.cottoncloud.dev/",
            string accountName = "user@example.test",
            string localPath = "/home/user/Documents",
            string remotePath = "/Documents")
        {
            return new DesktopDiagnosticsBundle(
                DateTimeOffset.Parse("2026-06-03T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                "1.0.0",
                serverUrl,
                accountName,
                new DesktopDataPathSnapshot(
                    paths.DataDirectory,
                    paths.AppDatabasePath,
                    paths.SyncStateDatabasePath,
                    paths.TokenStorePath),
                [
                    new DesktopSyncPairSnapshot(
                        Guid.NewGuid(),
                        "Documents",
                        localPath,
                        remotePath,
                        "Idle"),
                ],
                syncState ?? new SyncStateStoreDiagnostics(
                    FileSizeBytes: 4096,
                    PageCount: 1,
                    FreelistCount: 0,
                    PageSizeBytes: 4096,
                    SyncEntryCount: 1,
                    SyncChangeCursorCount: 1),
                runtimeHealth ?? new DesktopRuntimeHealthSnapshot(
                    ProcessId: 1,
                    ProcessName: "Cotton.Sync.Desktop",
                    WorkingSetBytes: 1024,
                    PrivateMemoryBytes: 2048,
                    ThreadCount: 4,
                    HandleCount: 8),
                syncLifecycle ?? new DesktopSyncLifecycleDiagnosticsSnapshot(
                    IsSignedIn: true,
                    SyncCoreState: "running",
                    IsBackgroundActive: true,
                    SyncPairCount: 1,
                    EnabledSyncPairCount: 1,
                    HasNoSyncPairs: false,
                    IsZeroPairBackgroundActive: false,
                    Status: "configuredPairs",
                    Details: "Signed in with configured sync pairs."),
                notification ?? new DesktopNotificationDiagnosticsSnapshot(
                    Platform: "Unsupported",
                    AdapterName: "Unsupported",
                    IsSupported: false,
                    IsDeliveryExecutableAvailable: false,
                    IsIconAvailable: false,
                    AppName: "Cotton Sync",
                    AppUserModelId: null,
                    IsInstalledAppIdentityVerified: false,
                    IdentityStatus: "unsupported",
                    Details: "Desktop notifications are not fully available."),
                update ?? DesktopUpdateDiagnosticsSnapshot.NotChecked("1.0.0"),
                cloudFilesRegistration ?? new DesktopCloudFilesRegistrationDiagnosticsSnapshot(
                    IsWindows: false,
                    IsStorageProviderHelperAvailable: false,
                    IsStorageProviderSupported: null,
                    VirtualFilesSyncPairCount: 0,
                    RegisteredSyncPairCount: 0,
                    MissingSyncPairCount: 0,
                    UnknownSyncPairCount: 0,
                    []),
                selfTestItems ?? [
                    new DesktopSelfTestItemSnapshot("Server identity", true, "Cotton Cloud"),
                ],
                cloudFilesEvents ?? []);
        }

        private static string ReadEntry(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(
                "Diagnostics archive entry is missing: " + entryName);
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
