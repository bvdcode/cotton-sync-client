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
            using StringWriter output = new StringWriter();
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
            using StringWriter output = new StringWriter();

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
            using StringWriter output = new StringWriter();

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
        public async Task RunSocketCleanupSmokeAsync_SuppressesExpectedSocketAbortEvents()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                ["--data-dir", _tempDirectory, "--socket-cleanup-smoke"]);
            using StringWriter output = new();

            int exitCode = await DesktopCommandLineRunner.RunSocketCleanupSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output);

            string report = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(report, Does.Contain("Cotton Sync Desktop socket cleanup smoke"));
                Assert.That(report, Does.Contain("ExpectedCleanupEventsObserved: 3/3"));
                Assert.That(report, Does.Contain("UnexpectedUnobservedSocketCleanupLog: false"));
                Assert.That(report, Does.Contain("Result: passed"));
            });
        }



    }
}
