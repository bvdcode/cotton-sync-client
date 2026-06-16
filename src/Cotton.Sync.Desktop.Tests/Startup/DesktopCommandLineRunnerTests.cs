// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.VirtualFiles;

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
        public async Task RunCloudFilesCleanupAsync_UnregistersOnlyVirtualFilesPairs()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings fullMirror = CreateSyncPair("Full", SyncPairMode.FullMirror, Path.Combine(_tempDirectory, "full"));
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(fullMirror);
            await store.UpsertAsync(virtualFiles);
            var adapter = new FakeCloudFilesAdapter();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter);
            IReadOnlyList<SyncPairSettings> remainingPairs = await store.ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(remainingPairs.Select(static pair => pair.Id), Is.EquivalentTo(new[] { fullMirror.Id, virtualFiles.Id }));
                Assert.That(output.ToString(), Does.Contain("Roots cleaned: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenUnregisterFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(virtualFiles);
            var adapter = new FakeCloudFilesAdapter
            {
                Exception = new InvalidOperationException("unregister failed"),
            };
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
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

        private static SyncPairSettings CreateSyncPair(string displayName, SyncPairMode mode, string localRootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                LocalRootPath = localRootPath,
                RemoteDisplayPath = "/" + displayName,
                RemoteRootNodeId = Guid.NewGuid(),
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<SyncPairSettings> UnregisteredPairs { get; } = [];

            public Exception? Exception { get; set; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                UnregisteredPairs.Add(syncPair);
                if (Exception is not null)
                {
                    throw Exception;
                }
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
