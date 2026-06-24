// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class DesktopStartupOptionsTests
    {
        [Test]
        public void Parse_LoadsServerUrlAndUsername()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--server-url",
                    "https://app.cottoncloud.dev/",
                    "--username=desktop@example.test",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.ServerUrl, Is.EqualTo(new Uri("https://app.cottoncloud.dev/")));
                Assert.That(options.Username, Is.EqualTo("desktop@example.test"));
                Assert.That(options.DataDirectory, Is.Null);
                Assert.That(options.StartMinimizedToTray, Is.False);
                Assert.That(options.RunSelfTest, Is.False);
            });
        }

        [Test]
        public void Parse_IgnoresUnsupportedServerUrl()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--server-url",
                    "file:///tmp/cotton",
                    "--username",
                    " desktop@example.test ",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.ServerUrl, Is.Null);
                Assert.That(options.Username, Is.EqualTo("desktop@example.test"));
            });
        }

        [Test]
        public void Parse_NormalizesBareServerHostToHttps()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--server",
                    "app.cottoncloud.dev",
                ]);

            Assert.That(options.ServerUrl, Is.EqualTo(new Uri("https://app.cottoncloud.dev/")));
        }

        [Test]
        public void Parse_LoadsStartMinimizedFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--start-minimized",
                ]);

            Assert.That(options.StartMinimizedToTray, Is.True);
        }

        [Test]
        public void Parse_LoadsSelfTestFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--self-test",
                ]);

            Assert.That(options.RunSelfTest, Is.True);
        }

        [Test]
        public void Parse_LoadsExportDiagnosticsFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--export-diagnostics",
                ]);

            Assert.That(options.ExportDiagnostics, Is.True);
            Assert.That(options.ExportPrivateSupportDiagnostics, Is.False);
        }

        [Test]
        public void Parse_LoadsPrivateSupportDiagnosticsFlagAsExplicitExportMode()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--export-diagnostics-private",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.ExportDiagnostics, Is.True);
                Assert.That(options.ExportPrivateSupportDiagnostics, Is.True);
            });
        }

        [Test]
        public void Parse_LoadsCloudFilesCleanupFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--cleanup-cloud-files"]);

            Assert.That(options.CleanupCloudFiles, Is.True);
        }

        [Test]
        public void Parse_LoadsWindowsVirtualFilesSmokeFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--windows-virtual-files-smoke"]);

            Assert.That(options.RunWindowsVirtualFilesSmoke, Is.True);
        }

        [Test]
        public void Parse_LoadsWindowsVirtualFilesSmokeAlias()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--vfs-smoke"]);

            Assert.That(options.RunWindowsVirtualFilesSmoke, Is.True);
        }

        [Test]
        public void Parse_LoadsSocketCleanupSmokeFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--socket-cleanup-smoke"]);

            Assert.That(options.RunSocketCleanupSmoke, Is.True);
        }

        [Test]
        public void Parse_LoadsWindowsVirtualFilesSmokeHoldSeconds()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--vfs-smoke-hold-after-placeholder-seconds",
                    "15",
                ]);

            Assert.That(options.WindowsVirtualFilesSmokeHoldAfterPlaceholder, Is.EqualTo(TimeSpan.FromSeconds(15)));
        }

        [Test]
        public void Parse_LoadsWindowsVirtualFilesSmokePhase()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--vfs-smoke-phase",
                    "reconnect-existing",
                ]);

            Assert.That(options.WindowsVirtualFilesSmokePhase, Is.EqualTo("reconnect-existing"));
        }

        [Test]
        public void Parse_LoadsWindowsVirtualFilesSmokePlaceholderCount()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--vfs-smoke-placeholder-count",
                    "100000",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunWindowsVirtualFilesSmoke, Is.True);
                Assert.That(options.WindowsVirtualFilesSmokePlaceholderCount, Is.EqualTo(100_000));
            });
        }

        [Test]
        public void Parse_LoadsVersionFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--version"]);

            Assert.That(options.PrintVersion, Is.True);
        }

        [Test]
        public void Parse_LoadsShellCopyShareLinkTargetPath()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--copy-shell-share-link",
                    @"C:\Cloud\Docs\report.pdf",
                ]);

            Assert.That(options.ShellCopyShareLinkTargetPath, Is.EqualTo(@"C:\Cloud\Docs\report.pdf"));
        }

        [Test]
        public void Parse_LoadsVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "settings",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.Settings));
        }

        [Test]
        public void Parse_LoadsProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.Progress));
        }

        [Test]
        public void Parse_LoadsLongProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "long-progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.LongProgress));
        }

        [Test]
        public void Parse_LoadsManySmallDownloadVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "many-small-download",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.ManySmallDownload));
        }

        [Test]
        public void Parse_LoadsHighPressureStartingVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "high-pressure-starting",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.HighPressureStarting));
        }

        [Test]
        public void Parse_LoadsVirtualFilesSeedingVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "virtual-files-seeding",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.VirtualFilesSeeding));
        }

        [Test]
        public void Parse_LoadsUpdateDownloadProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "update-download-progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.UpdateDownloadProgress));
        }

        [Test]
        public void Parse_LoadsUpdateInstallProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "update-install-progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.UpdateInstallProgress));
        }

        [Test]
        public void Parse_LoadsHyphenatedVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "add-folder",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.AddFolder));
        }

        [Test]
        public void Parse_LoadsFolderControlsVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "folder-controls",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.FolderControls));
        }

        [Test]
        public void Parse_LoadsEmptyDashboardVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "empty-dashboard",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.EmptyDashboard));
        }

        [Test]
        public void Parse_LoadsSignInErrorVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "sign-in-error",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.SignInError));
        }

        [Test]
        public void Parse_LoadsMissingLocalRootVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "missing-local-root",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.MissingLocalRoot));
        }

        [Test]
        public void Parse_LoadsOfflineVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "offline",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.Offline));
        }

        [Test]
        public void Parse_LoadsAddFolderManyRemoteFoldersVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "add-folder-many-remote-folders",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.AddFolderManyRemoteFolders));
        }

        [Test]
        public void Parse_LoadsMultiWordVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "settings-diagnostics",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.SettingsDiagnostics));
        }

        [Test]
        public void Parse_LoadsScreenshotStateAlias()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--screenshot-state=conflict",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.Conflict));
        }

        [Test]
        public void Parse_IgnoresUnsupportedVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "production",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.Null);
        }

        [Test]
        public void Parse_LoadsDataDirectory()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--data-dir",
                    " /tmp/cotton-sync-smoke ",
                ]);

            Assert.That(options.DataDirectory, Is.EqualTo("/tmp/cotton-sync-smoke"));
        }

        [Test]
        public void Parse_LoadsLiveSyncSmokeOptions()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--server",
                    "app.cottoncloud.dev",
                    "--data-dir",
                    " C:\\Temp\\cotton-desktop-smoke ",
                    "--local-root",
                    " C:\\Temp\\cotton-desktop-a ",
                    "--second-local-root",
                    " C:\\Temp\\cotton-desktop-b ",
                    "--remote-path",
                    " /CottonSyncQa/Desktop ",
                    "--live-sync-smoke-approval-hold-seconds",
                    "7",
                    "--live-sync-smoke-preserve-existing-local-files",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunLiveSyncSmoke, Is.True);
                Assert.That(options.ServerUrl, Is.EqualTo(new Uri("https://app.cottoncloud.dev/")));
                Assert.That(options.DataDirectory, Is.EqualTo("C:\\Temp\\cotton-desktop-smoke"));
                Assert.That(options.LocalRoot, Is.EqualTo("C:\\Temp\\cotton-desktop-a"));
                Assert.That(options.SecondLocalRoot, Is.EqualTo("C:\\Temp\\cotton-desktop-b"));
                Assert.That(options.RemotePath, Is.EqualTo("/CottonSyncQa/Desktop"));
                Assert.That(options.LiveSyncSmokeApprovalHold, Is.EqualTo(TimeSpan.FromSeconds(7)));
                Assert.That(options.LiveSyncSmokePreserveExistingLocalFiles, Is.True);
                Assert.That(options.SyncMode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(options.SyncModeError, Is.Null);
            });
        }

        [Test]
        public void Parse_LoadsLiveSyncSmokeWindowsVirtualFilesMode()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--sync-mode",
                    "windows-virtual-files",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunLiveSyncSmoke, Is.True);
                Assert.That(options.SyncMode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                Assert.That(options.SyncModeError, Is.Null);
            });
        }

        [Test]
        public void Parse_ReportsInvalidLiveSyncSmokeMode()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--live-sync-smoke",
                    "--sync-mode",
                    "placeholder",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.SyncMode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(options.SyncModeError, Does.Contain("Unsupported sync mode"));
            });
        }

        [Test]
        public void Parse_LoadsDesktopLiveSyncSmokeAlias()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--desktop-live-sync-smoke"]);

            Assert.That(options.RunLiveSyncSmoke, Is.True);
        }

        [Test]
        public void Parse_LoadsUpdateDiscoverySmokeOptions()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-discovery-smoke",
                    "--update-manifest-url",
                    "http://127.0.0.1:8080/release-manifest.json",
                    "--expected-update-version",
                    "0.1.1",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunUpdateDiscoverySmoke, Is.True);
                Assert.That(
                    options.UpdateManifestUri,
                    Is.EqualTo(new Uri("http://127.0.0.1:8080/release-manifest.json")));
                Assert.That(options.ExpectedUpdateVersion, Is.EqualTo("0.1.1"));
            });
        }

        [Test]
        public void Parse_LoadsDesktopUpdateSmokeAlias()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--desktop-update-smoke"]);

            Assert.That(options.RunUpdateDiscoverySmoke, Is.True);
        }

        [Test]
        public void Parse_LoadsUpdateInstallSmokeOptions()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-install-smoke",
                    "--data-dir",
                    @"C:\CottonSyncQa\UpdateInstall",
                    "--update-installer-path",
                    @"C:\CottonSyncQa\UpdateInstall\CottonSync-Windows-Setup.cmd",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunUpdateInstallSmoke, Is.True);
                Assert.That(options.DataDirectory, Is.EqualTo(@"C:\CottonSyncQa\UpdateInstall"));
                Assert.That(
                    options.UpdateInstallerPath,
                    Is.EqualTo(@"C:\CottonSyncQa\UpdateInstall\CottonSync-Windows-Setup.cmd"));
            });
        }

        [Test]
        public void Parse_LoadsShellShareLinkSmokeOptions()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--shell-share-link-smoke",
                    "--data-dir",
                    @"C:\CottonSyncQa\ShellShareLink",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunShellShareLinkSmoke, Is.True);
                Assert.That(options.DataDirectory, Is.EqualTo(@"C:\CottonSyncQa\ShellShareLink"));
            });
        }

        [Test]
        public void Parse_DoesNotTreatNextFlagAsOptionValue()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--data-dir",
                    "--self-test",
                    "--server-url",
                    "--username",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.DataDirectory, Is.Null);
                Assert.That(options.ServerUrl, Is.Null);
                Assert.That(options.Username, Is.Null);
                Assert.That(options.RunSelfTest, Is.True);
            });
        }
    }
}
