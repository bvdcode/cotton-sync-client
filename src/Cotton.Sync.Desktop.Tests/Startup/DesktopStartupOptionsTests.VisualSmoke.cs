// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopStartupOptionsTests
    {
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
        public void Parse_LoadsConnectingVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "connecting",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.Connecting));
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
        public void Parse_LoadsHydrationProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "hydration-progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.HydrationProgress));
        }

        [Test]
        public void Parse_LoadsDehydrationProgressVisualSmokeScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "dehydration-progress",
                ]);

            Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.DehydrationProgress));
        }

        [Test]
        public void Parse_LoadsVisualSmokeScaleAlongsideScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "long-progress",
                    "--visual-scale",
                    "2",
                ]);

            Assert.Multiple(() =>
            {
                Assert.That(options.VisualSmokeScenario, Is.EqualTo(DesktopVisualSmokeScenario.LongProgress));
                Assert.That(options.VisualSmokeScale, Is.EqualTo(2));
            });
        }

        [TestCase("0.75")]
        [TestCase("4")]
        [TestCase("invalid")]
        public void Parse_IgnoresUnsupportedVisualSmokeScale(string value)
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-smoke",
                    "long-progress",
                    "--visual-scale",
                    value,
                ]);

            Assert.That(options.VisualSmokeScale, Is.Null);
        }

        [Test]
        public void Parse_IgnoresVisualSmokeScaleWithoutScenario()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--visual-scale",
                    "2",
                ]);

            Assert.That(options.VisualSmokeScale, Is.Null);
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
    }
}
