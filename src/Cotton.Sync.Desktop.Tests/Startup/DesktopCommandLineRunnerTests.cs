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
            var storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);
            IReadOnlyList<SyncPairSettings> remainingPairs = await store.ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(remainingPairs.Select(static pair => pair.Id), Is.EquivalentTo(new[] { fullMirror.Id, virtualFiles.Id }));
                Assert.That(output.ToString(), Does.Contain("Roots cleaned: 1"));
                Assert.That(output.ToString(), Does.Contain("Orphaned storage-provider roots cleaned."));
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
            var storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenOrphanedStorageProviderCleanupFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            var store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            var adapter = new FakeCloudFilesAdapter();
            var storageProvider = new FakeStorageProviderSyncRootRegistrar
            {
                Exception = new InvalidOperationException("orphan cleanup failed"),
            };
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failed orphaned storage-provider cleanup"));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        [Platform(Include = "Win")]
        public async Task RunWindowsVirtualFilesSmokeAsync_RejectsRootOutsideIsolatedQaDrive()
        {
            string unsafeRoot = Path.Combine(_tempDirectory, "vfs-root");
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--windows-virtual-files-smoke",
                    "--data-dir",
                    Path.Combine(_tempDirectory, "state"),
                    "--local-root",
                    unsafeRoot,
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunWindowsVirtualFilesSmokeAsync(
                DesktopAppPaths.CreateForDataDirectory(Path.Combine(_tempDirectory, "state")),
                options,
                output,
                new FakeCloudFilesAdapter());

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain(@"refuses to touch paths outside S:\CottonSyncVfsQa\..."));
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
                    "/CodexSyncQa/DesktopSmoke",
                    "--live-sync-smoke-preserve-existing-local-files",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("--local-root must be empty or missing"));
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
                    "/CodexSyncQa/DesktopSmoke",
                    "--sync-mode",
                    "placeholder",
                ]);
            using var output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Does.Contain("Unsupported sync mode"));
                Assert.That(Directory.Exists(firstLocalRoot), Is.False);
                Assert.That(Directory.Exists(secondLocalRoot), Is.False);
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

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
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

        private sealed class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            public int UnregisterAllCalls { get; private set; }

            public Exception? Exception { get; set; }

            public bool IsSupported()
            {
                return true;
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void Unregister(Guid syncPairId)
            {
                throw new NotSupportedException();
            }

            public void UnregisterAllForCurrentUser()
            {
                UnregisterAllCalls++;
                if (Exception is not null)
                {
                    throw Exception;
                }
            }
        }
    }
}
