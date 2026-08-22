// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
    public partial class DesktopDiagnosticsExporterTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            DesktopAuthDiagnosticsState.ResetForTests();
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            WindowsCloudFilesDiagnosticEvent cloudFilesEvent = new WindowsCloudFilesDiagnosticEvent(
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
        public async Task ExportAsync_SerializesUpdateDiagnosticsWithoutInstallerPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            string installerPath = Path.Combine(paths.UpdateCacheDirectory, "0.0.2", "CottonSync-Windows-Setup.exe");
            DesktopUpdateDiagnosticsSnapshot update = new DesktopUpdateDiagnosticsSnapshot(
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            const string localRoot = @"C:\Users\Person\Cotton\Virtual";
            DesktopCloudFilesRegistrationDiagnosticsSnapshot cloudFilesRegistration = new DesktopCloudFilesRegistrationDiagnosticsSnapshot(
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();

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
            DesktopAuthDiagnosticsSnapshot? auth = null,
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
                auth ?? DesktopAuthDiagnosticsSnapshot.Initial,
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
                cloudFilesEvents ?? [],
                [],
                []);
        }

        private static string ReadEntry(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(
                "Diagnostics archive entry is missing: " + entryName);
            using Stream stream = entry.Open();
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
