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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
        public async Task ExportAsync_RedactsHistoricalWindowsPathMissingFromCurrentBundle()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            const string historicalPath = @"C:\Users\Person\Removed pair\private-track.flac";
            File.WriteAllText(
                paths.LogFilePath,
                "Scanned local tree metadata for " + historicalPath + " with 0 directories and 1 files in 12 ms.");
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string exportedLog = ReadEntry(archive, "logs/cotton-sync.log");
            Assert.Multiple(() =>
            {
                Assert.That(exportedLog, Does.Contain("Scanned local tree metadata for [local-path]"));
                Assert.That(exportedLog, Does.Not.Contain(historicalPath));
                Assert.That(exportedLog, Does.Not.Contain("private-track.flac"));
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();

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
        public async Task ExportAsync_SanitizesAuthFailureMessageInPublicBundle()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            const string serverUrl = "https://private.cotton.example/";
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            DesktopAuthDiagnosticsSnapshot auth = DesktopAuthDiagnosticsSnapshot.Initial with
            {
                LastSessionRestoreStatus = "failed",
                LastSessionRestoreFailureType = "CottonApiException",
                LastSessionRestoreFailureMessage = "Failed for " + serverUrl + " with refreshToken=secret-refresh",
            };

            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths, serverUrl: serverUrl, auth: auth));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement authJson = document.RootElement.GetProperty("auth");

            Assert.Multiple(() =>
            {
                Assert.That(authJson.GetProperty("lastSessionRestoreStatus").GetString(), Is.EqualTo("failed"));
                Assert.That(authJson.GetProperty("lastSessionRestoreFailureType").GetString(), Is.EqualTo("CottonApiException"));
                Assert.That(authJson.GetProperty("lastSessionRestoreFailureMessage").GetString(), Does.Contain("[server-url]"));
                Assert.That(authJson.GetProperty("lastSessionRestoreFailureMessage").GetString(), Does.Contain("refreshToken=[redacted]"));
                Assert.That(diagnosticsJson, Does.Not.Contain(serverUrl));
                Assert.That(diagnosticsJson, Does.Not.Contain("secret-refresh"));
            });
        }

        [Test]
        public async Task ExportAsync_SerializesStateAndRuntimeHealthMetrics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            SyncStateStoreDiagnostics syncState = new SyncStateStoreDiagnostics(
                FileSizeBytes: 8192,
                PageCount: 2,
                FreelistCount: 1,
                PageSizeBytes: 4096,
                SyncEntryCount: 3,
                SyncChangeCursorCount: 1);
            DesktopRuntimeHealthSnapshot runtimeHealth = new DesktopRuntimeHealthSnapshot(
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            DesktopSyncLifecycleDiagnosticsSnapshot syncLifecycle = new DesktopSyncLifecycleDiagnosticsSnapshot(
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
            DesktopDiagnosticsExporter exporter = new DesktopDiagnosticsExporter();
            DesktopNotificationDiagnosticsSnapshot notification = new DesktopNotificationDiagnosticsSnapshot(
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
            DesktopSelfTestItemSnapshot[] selfTestItems = new[]
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

    }
}
