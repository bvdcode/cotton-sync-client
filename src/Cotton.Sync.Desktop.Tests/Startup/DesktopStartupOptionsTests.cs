// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

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
        }

        [Test]
        public void Parse_LoadsCloudFilesCleanupFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--cleanup-cloud-files"]);

            Assert.That(options.CleanupCloudFiles, Is.True);
        }

        [Test]
        public void Parse_LoadsVersionFlag()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--version"]);

            Assert.That(options.PrintVersion, Is.True);
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
                    " /CodexSyncQa/Desktop ",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunLiveSyncSmoke, Is.True);
                Assert.That(options.ServerUrl, Is.EqualTo(new Uri("https://app.cottoncloud.dev/")));
                Assert.That(options.DataDirectory, Is.EqualTo("C:\\Temp\\cotton-desktop-smoke"));
                Assert.That(options.LocalRoot, Is.EqualTo("C:\\Temp\\cotton-desktop-a"));
                Assert.That(options.SecondLocalRoot, Is.EqualTo("C:\\Temp\\cotton-desktop-b"));
                Assert.That(options.RemotePath, Is.EqualTo("/CodexSyncQa/Desktop"));
            });
        }

        [Test]
        public void Parse_LoadsDesktopLiveSyncSmokeAlias()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--desktop-live-sync-smoke"]);

            Assert.That(options.RunLiveSyncSmoke, Is.True);
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
