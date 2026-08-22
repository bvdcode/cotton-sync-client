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
        public async Task LoadAsync_IncludesDiagnosticsFieldsForSyncPairs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Guid syncPairId = Guid.NewGuid();
            Guid remoteRootNodeId = Guid.NewGuid();
            DateTime lastSyncedAtUtc = new(2026, 6, 3, 12, 30, 0, DateTimeKind.Utc);
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            Directory.CreateDirectory(Path.Combine(_tempDirectory, "Documents"));
            await syncPairStore.UpsertAsync(new SyncPairSettings
            {
                Id = syncPairId,
                DisplayName = "Documents",
                LocalRootPath = Path.Combine(_tempDirectory, "Documents"),
                RemoteRootNodeId = remoteRootNodeId,
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPairId.ToString(),
                RelativePath = "file.txt",
                Kind = SyncEntryKind.File,
                SyncedAtUtc = lastSyncedAtUtc,
            });
            await stateStore.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = syncPairId.ToString(),
                LastCursor = 42,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            DesktopSyncPairSnapshot syncPair = snapshot.SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(syncPair.RemoteRootNodeId, Is.EqualTo(remoteRootNodeId));
                Assert.That(syncPair.LastSyncedAtUtc, Is.EqualTo(lastSyncedAtUtc));
                Assert.That(syncPair.ChangeCursor, Is.EqualTo(42));
                Assert.That(syncPair.LastError, Is.Null);
            });
        }

        [Test]
        public async Task LoadAsync_IncludesNotificationPreference()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            AppPreferences preferences = await preferencesStore.GetAsync();
            preferences.EnableNotifications = false;
            await preferencesStore.SaveAsync(preferences);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.That(snapshot.EnableNotifications, Is.False);
        }

        [Test]
        public async Task LoadAsync_IncludesDataPathsForDiagnostics()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.DataPaths.DataDirectory, Is.EqualTo(paths.DataDirectory));
                Assert.That(snapshot.DataPaths.AppDatabasePath, Is.EqualTo(paths.AppDatabasePath));
                Assert.That(snapshot.DataPaths.SyncStateDatabasePath, Is.EqualTo(paths.SyncStateDatabasePath));
                Assert.That(snapshot.DataPaths.TokenStorePath, Is.EqualTo(paths.TokenStorePath));
            });
        }

        [Test]
        public async Task LoadAsync_InitializesSyncStateDatabaseForNewProfile()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            await controller.LoadAsync();

            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            SyncChangeCursor cursor = await stateStore.GetChangeCursorAsync("new-profile");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(paths.SyncStateDatabasePath), Is.True);
                Assert.That(cursor.LastCursor, Is.Zero);
            });
        }

        [Test]
        public async Task LoadAsync_ReturnsEmptySignInHintsForNewPreferences()
        {
            using DesktopShellController controller = CreateController();

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ServerUrl, Is.Null);
                Assert.That(snapshot.RememberedUsername, Is.Null);
                Assert.That(snapshot.IsSignedIn, Is.False);
            });
        }

        [Test]
        public async Task LoadAsync_ReturnsRememberedSignInHintsWithoutStoredSession()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = new Uri("https://cotton.example.test/"),
                RememberedUsername = "desktop@example.test",
            });
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ServerUrl, Is.EqualTo(new Uri("https://cotton.example.test/")));
                Assert.That(snapshot.RememberedUsername, Is.EqualTo("desktop@example.test"));
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.AccountName, Is.Null);
            });
        }

        [Test]
        public async Task LoadAsync_IncludesThemePreference()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            AppPreferences preferences = await preferencesStore.GetAsync();
            preferences.ThemeMode = AppThemeMode.Dark;
            await preferencesStore.SaveAsync(preferences);
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.That(snapshot.ThemeMode, Is.EqualTo(AppThemeMode.Dark));
        }

        [Test]
        public async Task SetNotificationsEnabledAsync_PersistsPreference()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            await controller.SetNotificationsEnabledAsync(false);

            AppPreferences preferences = await preferencesStore.GetAsync();
            Assert.That(preferences.EnableNotifications, Is.False);
        }

        [Test]
        public async Task SetThemeModeAsync_PersistsPreference()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            using DesktopShellController controller = CreateController(paths, new SqliteSyncPairSettingsStore(paths.AppDatabasePath));

            await controller.SetThemeModeAsync(AppThemeMode.Light);

            AppPreferences preferences = await preferencesStore.GetAsync();
            Assert.That(preferences.ThemeMode, Is.EqualTo(AppThemeMode.Light));
        }

        [Test]
        public async Task SetSyncPairEnabledAsync_PersistsEnabledState()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            await controller.SetSyncPairEnabledAsync(syncPair.Id, enabled: false);

            SyncPairSettings? persisted = await syncPairStore.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.IsEnabled, Is.False);
                Assert.That(persisted.UpdatedAtUtc, Is.GreaterThan(syncPair.UpdatedAtUtc));
            });
        }

        [Test]
        public async Task RenameSyncPairAsync_PersistsDisplayName()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            await controller.RenameSyncPairAsync(syncPair.Id, "  Work documents  ");

            SyncPairSettings? persisted = await syncPairStore.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.DisplayName, Is.EqualTo("Work documents"));
                Assert.That(persisted.UpdatedAtUtc, Is.GreaterThan(syncPair.UpdatedAtUtc));
            });
        }

        [Test]
        public async Task RemoveSyncPairAsync_DeletesConfiguredPair()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            Directory.CreateDirectory(syncPair.LocalRootPath);
            string localFilePath = Path.Combine(syncPair.LocalRootPath, "keep-local-file.txt");
            await File.WriteAllTextAsync(localFilePath, "keep me local");
            await syncPairStore.UpsertAsync(syncPair);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString(),
                RelativePath = "synced.txt",
                Kind = SyncEntryKind.File,
            });
            await stateStore.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString(),
                LastCursor = 42,
            });
            using DesktopShellController controller = CreateController(paths, syncPairStore);

            await controller.RemoveSyncPairAsync(syncPair.Id);

            SyncPairSettings? persisted = await syncPairStore.GetAsync(syncPair.Id);
            IReadOnlyList<SyncStateEntry> entries = await stateStore.LoadPairAsync(syncPair.Id.ToString());
            SyncChangeCursor cursor = await stateStore.GetChangeCursorAsync(syncPair.Id.ToString());

            Assert.Multiple(() =>
            {
                Assert.That(persisted, Is.Null);
                Assert.That(entries, Is.Empty);
                Assert.That(cursor.LastCursor, Is.Zero);
                Assert.That(Directory.Exists(syncPair.LocalRootPath), Is.True);
                Assert.That(File.Exists(localFilePath), Is.True);
                Assert.That(File.ReadAllText(localFilePath), Is.EqualTo("keep me local"));
            });
        }

    }
}
