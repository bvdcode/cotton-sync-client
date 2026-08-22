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
        [Platform(Include = "Win")]
        public async Task RunWindowsVirtualFilesSmokeAsync_RejectsDriveRoot()
        {
            string unsafeRoot = Path.GetPathRoot(_tempDirectory) ?? @"C:\";
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--data-dir",
                    Path.Combine(_tempDirectory, "state"),
                    "--local-root",
                    unsafeRoot,
                ]);
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunWindowsVirtualFilesSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state")),
                options,
                output,
                new FakeCloudFilesAdapter());

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("cannot be a drive or share root"));
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
            using StringWriter output = new StringWriter();

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
            using StringWriter output = new StringWriter();

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
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--local-root must be empty or missing"));
            });
        }

        [Test]
        public async Task RunLiveSyncSmokeAsync_RejectsSeedCountWithoutPreserveFlag()
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
                    "--live-sync-smoke-seed-file-count",
                    "64",
                ]);
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--live-sync-smoke-preserve-existing-local-files"));
                Assert.That(Directory.Exists(firstLocalRoot), Is.False);
                Assert.That(Directory.Exists(secondLocalRoot), Is.False);
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
            using StringWriter output = new StringWriter();

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
            FakeDesktopUpdateService updateService = new FakeDesktopUpdateService(DesktopAppPaths.CreateForDataDirectory(_tempDirectory));
            using StringWriter output = new StringWriter();

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
            FakeDesktopUpdateService updateService = new FakeDesktopUpdateService(paths);
            using StringWriter output = new StringWriter();

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
            FakeDesktopUpdateInstaller installer = new FakeDesktopUpdateInstaller();
            using StringWriter output = new StringWriter();

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
            FakeDesktopUpdateInstaller installer = new FakeDesktopUpdateInstaller();
            using StringWriter output = new StringWriter();

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

        [Test]
        [Platform(Include = "Win")]
        public async Task RunUpdateInstallSmokeAsync_DefaultInstallerAllowsLocalSmokeCommand()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string installerPath = Path.Combine(_tempDirectory, "CottonSync-Windows-Setup.cmd");
            File.WriteAllText(installerPath, "@echo off\r\nexit /b 0\r\n");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--update-install-smoke",
                    "--data-dir",
                    _tempDirectory,
                    "--update-installer-path",
                    installerPath,
                ]);
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunUpdateInstallSmokeAsync(
                paths,
                options,
                output);

            string report = output.ToString();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(report, Does.Contain("PASS: Update installer launch returns a process id"));
                Assert.That(report, Does.Contain("PASS: Update installer startup probe does not fail"));
                Assert.That(report, Does.Contain("Result: passed"));
            });
        }
    }
}
