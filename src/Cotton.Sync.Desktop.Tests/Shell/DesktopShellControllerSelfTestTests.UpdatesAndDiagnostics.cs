// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerSelfTestTests
    {
        [Test]
        public async Task ExportDiagnosticsAsync_UsesInformationalAppVersion()
        {
            using DesktopShellController controller = CreateController();

            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            Assert.That(document.RootElement.GetProperty("appVersion").GetString(), Is.EqualTo(DesktopAppVersion.Current));
        }

        [Test]
        public async Task ExportDiagnosticsAsync_IncludesPublicSafeDataPathMetadata()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            string archivePath = await controller.ExportDiagnosticsAsync();

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
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.DataDirectory));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.AppDatabasePath));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.SyncStateDatabasePath));
                Assert.That(diagnosticsJson, Does.Not.Contain(paths.TokenStorePath));
            });
        }

        [Test]
        public async Task CheckForUpdateAsync_ReturnsAvailableUpdateDetails()
        {
            FakeUpdateService updateService = new FakeUpdateService(CreateUpdateCheckResult(isUpdateAvailable: true));
            using DesktopShellController controller = CreateController(updateService: updateService);

            DesktopUpdateStatusSnapshot result = await controller.CheckForUpdateAsync();

            Assert.Multiple(() =>
            {
                Assert.That(updateService.CheckCalls, Is.EqualTo(1));
                Assert.That(result.IsUpdateAvailable, Is.True);
                Assert.That(result.IsInstallerReady, Is.False);
                Assert.That(result.CurrentVersion, Is.EqualTo("0.0.1"));
                Assert.That(result.LatestVersion, Is.EqualTo("0.0.2"));
                Assert.That(result.Details, Is.EqualTo("Update 0.0.2 is available."));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsLastManualUpdateCheckOutcome()
        {
            FakeUpdateService updateService = new FakeUpdateService(CreateUpdateCheckResult(isUpdateAvailable: true));
            using DesktopShellController controller = CreateController(updateService: updateService);

            await controller.CheckForUpdateAsync();
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("succeeded"));
                Assert.That(update.GetProperty("lastCheckSource").GetString(), Is.EqualTo("manual"));
                Assert.That(update.GetProperty("currentVersion").GetString(), Is.EqualTo("0.0.1"));
                Assert.That(update.GetProperty("latestVersion").GetString(), Is.EqualTo("0.0.2"));
                Assert.That(update.GetProperty("isUpdateAvailable").GetBoolean(), Is.True);
                Assert.That(update.GetProperty("hasInstallerAsset").GetBoolean(), Is.True);
                Assert.That(update.GetProperty("isInstallerReady").GetBoolean(), Is.False);
                Assert.That(update.GetProperty("isUpdateCacheDirectoryPresent").GetBoolean(), Is.False);
                Assert.That(update.GetProperty("hasPendingUpdate").GetBoolean(), Is.False);
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsLastPeriodicUpdateCheckOutcome()
        {
            FakeUpdateService updateService = new FakeUpdateService(CreateUpdateCheckResult(isUpdateAvailable: true));
            using DesktopShellController controller = CreateController(updateService: updateService);

            await controller.CheckForUpdateAsync(DesktopUpdateCheckSource.Periodic);
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("succeeded"));
                Assert.That(update.GetProperty("lastCheckSource").GetString(), Is.EqualTo("periodic"));
                Assert.That(update.GetProperty("latestVersion").GetString(), Is.EqualTo("0.0.2"));
                Assert.That(update.GetProperty("isUpdateAvailable").GetBoolean(), Is.True);
            });
        }

        [Test]
        public async Task DownloadUpdateAsync_ReturnsReadyInstallerPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");
            FakeUpdateService updateService = new FakeUpdateService(
                CreateUpdateCheckResult(isUpdateAvailable: true),
                CreateUpdateDownloadResult(installerPath));
            using DesktopShellController controller = CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                updateService: updateService);

            DesktopUpdateStatusSnapshot result = await controller.DownloadUpdateAsync();
            DesktopPendingUpdate? pending = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory).TryLoad();

            Assert.Multiple(() =>
            {
                Assert.That(updateService.CheckCalls, Is.EqualTo(1));
                Assert.That(updateService.DownloadCalls, Is.EqualTo(1));
                Assert.That(result.IsInstallerReady, Is.True);
                Assert.That(result.InstallerPath, Is.EqualTo(installerPath));
                Assert.That(
                    result.Details,
                    Is.EqualTo(
                        "Update 0.0.2 is ready. Click Update to install it now, or it will install automatically on next app start."));
                Assert.That(pending?.Version, Is.EqualTo("0.0.2"));
                Assert.That(pending?.InstallerPath, Is.EqualTo(installerPath));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsStartupUpdateDownloadOutcome()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");
            FakeUpdateService updateService = new FakeUpdateService(
                CreateUpdateCheckResult(isUpdateAvailable: true),
                CreateUpdateDownloadResult(installerPath));
            using DesktopShellController controller = CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                updateService: updateService);

            await controller.DownloadUpdateAsync(DesktopUpdateCheckSource.Startup);
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("succeeded"));
                Assert.That(update.GetProperty("lastCheckSource").GetString(), Is.EqualTo("startup"));
                Assert.That(update.GetProperty("isInstallerReady").GetBoolean(), Is.True);
                Assert.That(update.GetProperty("hasPendingUpdate").GetBoolean(), Is.True);
                Assert.That(diagnosticsJson, Does.Not.Contain(installerPath));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsDownloadedUpdateCacheStateWithoutInstallerPath()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");
            FakeUpdateService updateService = new FakeUpdateService(
                CreateUpdateCheckResult(isUpdateAvailable: true),
                CreateUpdateDownloadResult(installerPath));
            using DesktopShellController controller = CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                updateService: updateService);

            await controller.DownloadUpdateAsync();
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("succeeded"));
                Assert.That(update.GetProperty("lastCheckSource").GetString(), Is.EqualTo("download"));
                Assert.That(update.GetProperty("isInstallerReady").GetBoolean(), Is.True);
                Assert.That(update.GetProperty("hasPendingUpdate").GetBoolean(), Is.True);
                Assert.That(update.GetProperty("pendingVersion").GetString(), Is.EqualTo("0.0.2"));
                Assert.That(update.GetProperty("pendingInstallerSizeBytes").GetInt64(), Is.EqualTo(1024));
                Assert.That(diagnosticsJson, Does.Not.Contain(installerPath));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsFailedUpdateCheckOutcome()
        {
            FakeUpdateService updateService = new FakeUpdateService(
                CreateUpdateCheckResult(isUpdateAvailable: false),
                checkException: new HttpRequestException("release manifest unavailable"));
            using DesktopShellController controller = CreateController(updateService: updateService);

            _ = Assert.ThrowsAsync<HttpRequestException>(() => controller.CheckForUpdateAsync());
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastCheckStatus").GetString(), Is.EqualTo("failed"));
                Assert.That(update.GetProperty("lastCheckSource").GetString(), Is.EqualTo("manual"));
                Assert.That(update.GetProperty("failureType").GetString(), Is.EqualTo(nameof(HttpRequestException)));
                Assert.That(update.GetProperty("failureMessage").GetString(), Does.Contain("release manifest unavailable"));
            });
        }

        [Test]
        public async Task InstallDownloadedUpdateAsync_StartsSilentInstallerWithRelaunch()
        {
            FakeUpdateInstaller updateInstaller = new FakeUpdateInstaller();
            using DesktopShellController controller = CreateController(updateInstaller: updateInstaller);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");

            await controller.InstallDownloadedUpdateAsync(installerPath);

            Assert.Multiple(() =>
            {
                Assert.That(updateInstaller.InstallerPath, Is.EqualTo(installerPath));
                Assert.That(updateInstaller.LaunchAfterUpdate, Is.True);
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsUpdateInstallerLaunchOutcomeWithoutInstallerPath()
        {
            FakeUpdateInstaller updateInstaller = new FakeUpdateInstaller
            {
                Result = new DesktopUpdateInstallResult(1234, ExitedDuringStartupProbe: false, ExitCode: null),
            };
            using DesktopShellController controller = CreateController(updateInstaller: updateInstaller);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");

            await controller.InstallDownloadedUpdateAsync(installerPath);
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastInstallLaunchStatus").GetString(), Is.EqualTo("launched"));
                Assert.That(update.GetProperty("lastInstallProcessId").GetInt32(), Is.EqualTo(1234));
                Assert.That(update.GetProperty("lastInstallExitedDuringStartupProbe").GetBoolean(), Is.False);
                Assert.That(update.GetProperty("lastInstallExitCode").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(update.GetProperty("lastInstallFailureType").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(diagnosticsJson, Does.Not.Contain(installerPath));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsUpdateInstallerLaunchFailureWithoutInstallerPath()
        {
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.exe");
            FakeUpdateInstaller updateInstaller = new FakeUpdateInstaller
            {
                Exception = new InvalidOperationException("Installer failed at " + installerPath),
            };
            using DesktopShellController controller = CreateController(updateInstaller: updateInstaller);

            _ = Assert.ThrowsAsync<InvalidOperationException>(() => controller.InstallDownloadedUpdateAsync(installerPath));
            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement update = document.RootElement.GetProperty("update");

            Assert.Multiple(() =>
            {
                Assert.That(update.GetProperty("lastInstallLaunchStatus").GetString(), Is.EqualTo("failed"));
                Assert.That(update.GetProperty("lastInstallFailureType").GetString(), Is.EqualTo(nameof(InvalidOperationException)));
                Assert.That(update.GetProperty("lastInstallFailureMessage").GetString(), Does.Contain("Installer failed"));
                Assert.That(update.GetProperty("lastInstallProcessId").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(diagnosticsJson, Does.Not.Contain(installerPath));
            });
        }
    }
}
