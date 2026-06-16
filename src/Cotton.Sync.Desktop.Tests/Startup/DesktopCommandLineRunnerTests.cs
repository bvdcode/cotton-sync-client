// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Startup;

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
                Assert.That(File.Exists(bundlePath), Is.True);
                Assert.That(Path.GetDirectoryName(bundlePath), Is.EqualTo(Path.Combine(_tempDirectory, "diagnostics")));
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
                    "/CodexSyncQa/DesktopSmoke",
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
                    "/CodexSyncQa/DesktopSmoke",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--local-root must be empty or missing"));
            });
        }
    }
}
