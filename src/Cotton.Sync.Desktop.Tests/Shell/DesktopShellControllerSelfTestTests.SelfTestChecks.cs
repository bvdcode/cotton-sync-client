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
        public async Task RunSelfTestAsync_ReportsWindowsVirtualFilesCapability()
        {
            using DesktopShellController controller = CreateController();

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Windows virtual files");
            if (OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                if (item.Details.Contains("shell helper", StringComparison.Ordinal)
                    || item.Details.Contains("StorageProvider", StringComparison.Ordinal))
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(item.Passed, Is.False);
                        Assert.That(item.Skipped, Is.True);
                        Assert.That(item.Details, Does.Contain("Cloud Files API"));
                    });
                }
                else
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(item.Passed, Is.True);
                        Assert.That(item.Skipped, Is.False);
                        Assert.That(item.Details, Does.Contain("Cloud Files API"));
                    });
                }
            }
            else
            {
                Assert.Multiple(() =>
                {
                    Assert.That(item.Passed, Is.False);
                    Assert.That(item.Skipped, Is.True);
                    Assert.That(item.Details, Does.Contain("Windows"));
                });
            }
        }

        [Test]
        public async Task RunSelfTestAsync_IncludesNotificationIdentityDetails()
        {
            using DesktopShellController controller = CreateController();

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Notification adapter");
            Assert.Multiple(() =>
            {
                Assert.That(item.Passed || item.Skipped, Is.True);
                Assert.That(item.Details, Does.Contain("adapter: "));
                Assert.That(item.Details, Does.Contain("app name: Cotton Sync"));
                Assert.That(item.Details, Does.Contain("icon: "));
                if (OperatingSystem.IsWindows())
                {
                    Assert.That(item.Details, Does.Contain("PowerShell is only the toast delivery helper"));
                    Assert.That(item.Details, Does.Contain("Start Menu AppUserModelID shortcut: "));
                }
            });
        }

        [Test]
        public async Task RunSelfTestAsync_FailsTokenStorageWhenProtectorIsNotReleaseSecure()
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage = new DesktopTokenStorageCapabilitySnapshot(
                "restricted-file-v1",
                IsReleaseSecure: false,
                "Development fallback");
            using DesktopShellController controller = CreateController(tokenStorageCapabilities: () => tokenStorage);

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Token storage");
            Assert.Multiple(() =>
            {
                Assert.That(item.Passed, Is.False);
                Assert.That(item.Details, Does.Contain("not release secure"));
                Assert.That(result.Passed, Is.False);
            });
        }

        [Test]
        public async Task RunSelfTestAsync_PassesTokenStorageWhenProtectorIsReleaseSecure()
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage = new DesktopTokenStorageCapabilitySnapshot(
                "linux-secret-service-v1",
                IsReleaseSecure: true,
                "Linux Secret Service through secret-tool");
            using DesktopShellController controller = CreateController(tokenStorageCapabilities: () => tokenStorage);

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Token storage");
            Assert.Multiple(() =>
            {
                Assert.That(item.Passed, Is.True);
                Assert.That(item.Details, Is.EqualTo("Linux Secret Service through secret-tool"));
            });
        }

        [Test]
        public async Task RunSelfTestAsync_VerifiesLooseDesktopIconAsset()
        {
            using DesktopShellController controller = CreateController();

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot icon = result.Items.Single(static item => item.Name == "Desktop icon");
            Assert.Multiple(() =>
            {
                Assert.That(icon.Passed, Is.True);
                Assert.That(icon.Details, Does.EndWith(Path.Combine("Assets", "icon-192.png")));
            });
        }

        [Test]
        public async Task RunSelfTestAsync_VerifiesUpdateCacheIsWritable()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot updateCache = result.Items.Single(static item => item.Name == "Update cache");
            Assert.Multiple(() =>
            {
                Assert.That(updateCache.Passed, Is.True);
                Assert.That(updateCache.Details, Is.EqualTo(paths.UpdateCacheDirectory));
                Assert.That(Directory.Exists(paths.UpdateCacheDirectory), Is.True);
                Assert.That(Directory.EnumerateFiles(paths.UpdateCacheDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task RunSelfTestAsync_VerifiesSyncStateCursorStoreAndReportsStorageMetrics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Sync state database");
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            SyncChangeCursor cursor = await stateStore.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(item.Passed, Is.True);
                Assert.That(item.Details, Does.Contain("entries="));
                Assert.That(item.Details, Does.Contain("cursors="));
                Assert.That(item.Details, Does.Contain("file="));
                Assert.That(item.Details, Does.Contain("used="));
                Assert.That(item.Details, Does.Contain("free="));
                Assert.That(item.Details, Does.Not.Contain(paths.SyncStateDatabasePath));
                Assert.That(File.Exists(paths.SyncStateDatabasePath), Is.True);
                Assert.That(cursor.LastCursor, Is.Zero);
            });
        }

        [Test]
        public async Task RunSelfTestAsync_FailsWhenSyncStateDatabaseIsEmptyButMostlyFreelist()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertManyAsync(CreateLargePlaceholderStateEntries("pair-a"));
            await stateStore.ReplacePairAsync("pair-a", Array.Empty<SyncStateEntry>());
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Sync state database");
            Assert.Multiple(() =>
            {
                Assert.That(item.Passed, Is.False);
                Assert.That(result.Passed, Is.False);
                Assert.That(item.Details, Does.Contain("no sync entries or change cursors"));
                Assert.That(item.Details, Does.Contain("free SQLite pages"));
                Assert.That(item.Details, Does.Not.Contain(paths.SyncStateDatabasePath));
            });
        }

        [Test]
        public async Task RunSelfTestAsync_UsesReadableFailureDetails()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                autostartService: new ThrowingAutostartService(
                    new InvalidOperationException("SQLite Error 1: 'no such table: sync_change_cursors'.")));

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot item = result.Items.Single(static selfTestItem => selfTestItem.Name == "Autostart adapter");
            Assert.Multiple(() =>
            {
                Assert.That(item.Passed, Is.False);
                Assert.That(
                    item.Details,
                    Is.EqualTo("Local sync state database is unavailable. Run diagnostics and restart Cotton Sync."));
                Assert.That(item.Details, Does.Not.Contain("sync_change_cursors"));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_DoesNotRunSelfTestServerProbe()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            await using SlowServerInfoEndpoint server = new SlowServerInfoEndpoint(TimeSpan.FromSeconds(5));
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = server.BaseAddress,
            });
            using DesktopShellController controller = CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                serverProbeTimeout: TimeSpan.FromMilliseconds(50));

            string archivePath = await controller.ExportDiagnosticsAsync();

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement[] selfTestItems = document.RootElement
                .GetProperty("selfTestItems")
                .EnumerateArray()
                .ToArray();
            JsonElement diagnosticsExport = selfTestItems.Single(item => string.Equals(
                item.GetProperty("name").GetString(),
                "Diagnostics export",
                StringComparison.Ordinal));
            JsonElement notificationAdapter = selfTestItems.Single(item => string.Equals(
                item.GetProperty("name").GetString(),
                "Notification adapter",
                StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(server.ReceivedRequest, Is.False);
                Assert.That(selfTestItems.Select(static item => item.GetProperty("name").GetString()), Does.Not.Contain("Server identity"));
                Assert.That(diagnosticsExport.GetProperty("passed").GetBoolean(), Is.True);
                Assert.That(
                    diagnosticsExport.GetProperty("details").GetString(),
                    Is.EqualTo("Captured current diagnostics and read-only capability checks; self-test probes were not run."));
                Assert.That(notificationAdapter.GetProperty("details").GetString(), Does.Contain("adapter: "));
                Assert.That(notificationAdapter.GetProperty("details").GetString(), Does.Contain("app name: Cotton Sync"));
                Assert.That(notificationAdapter.GetProperty("passed").ValueKind, Is.AnyOf(JsonValueKind.True, JsonValueKind.False));
                Assert.That(notificationAdapter.GetProperty("skipped").ValueKind, Is.AnyOf(JsonValueKind.True, JsonValueKind.False));
            });
        }

        [Test]
        public async Task RunSelfTestAsync_IncludesLocalAndRemoteRootChecksForSyncPairs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string localRoot = Path.Combine(_tempDirectory, "Documents");
            Directory.CreateDirectory(localRoot);
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            await syncPairStore.UpsertAsync(new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = localRoot,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.Items.Select(static item => item.Name), Does.Contain("Local root: Documents"));
                Assert.That(result.Items.Select(static item => item.Name), Does.Contain("Remote root: Documents"));
                DesktopSelfTestItemSnapshot remoteRoot =
                    result.Items.Single(static item => item.Name == "Remote root: Documents");
                Assert.That(remoteRoot.Details, Is.EqualTo("Sign in to verify"));
                Assert.That(remoteRoot.Skipped, Is.True);
                Assert.That(result.Passed, Is.True);
            });
        }

        [Test]
        public async Task RunSelfTestAsync_ReportsMissingLocalRootAsReadableFailure()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            string missingLocalRoot = Path.Combine(_tempDirectory, "DeletedDocuments");
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            await syncPairStore.UpsertAsync(new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = missingLocalRoot,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            DesktopSelfTestItemSnapshot localRoot =
                result.Items.Single(static item => item.Name == "Local root: Documents");
            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(localRoot.Passed, Is.False);
                Assert.That(localRoot.Details, Is.EqualTo("Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync."));
            });
        }

    }
}
