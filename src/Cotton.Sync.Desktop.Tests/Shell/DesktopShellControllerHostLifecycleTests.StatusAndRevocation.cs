// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [Test]
        public async Task StatusChanged_ForwardsLastSuccessfulSyncTimestamp()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            List<DesktopSyncStatusSnapshot> statusEvents = new List<DesktopSyncStatusSnapshot>();
            controller.StatusChanged += (_, status) => statusEvents.Add(status);
            Guid syncPairId = Guid.NewGuid();
            DateTime completedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            await controller.LoadAsync();
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPairId,
                        "Documents",
                        SyncPairRunState.Idle,
                        null,
                        null,
                        DateTime.UtcNow,
                        completedAtUtc),
                ],
                DateTime.UtcNow));

            DesktopSyncPairStatusSnapshot pairStatus = statusEvents.Last().SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pairStatus.Id, Is.EqualTo(syncPairId));
                Assert.That(pairStatus.LastSyncedAtUtc, Is.EqualTo(completedAtUtc));
            });
        }

        [Test]
        public async Task LoadAsync_ClearsStoredSessionWhenRestoreIsForbidden()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.Forbidden,
                null,
                "Forbidden");
            QueueingDesktopSyncApplicationFactory factory = new(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.HasStoredSession, Is.False);
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.EqualTo(1));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StatusChanged_MapsWaitingRuntimeStateWithoutActionRequired()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            List<DesktopSyncStatusSnapshot> statusEvents = new List<DesktopSyncStatusSnapshot>();
            controller.StatusChanged += (_, status) => statusEvents.Add(status);
            Guid syncPairId = Guid.NewGuid();
            const string message = "Local file is not ready yet: Drafts/report.docx. Sync will retry.";

            await controller.LoadAsync();
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPairId,
                        "Documents",
                        SyncPairRunState.Waiting,
                        message,
                        message,
                        DateTime.UtcNow),
                ],
                DateTime.UtcNow));

            DesktopSyncPairStatusSnapshot pairStatus = statusEvents.Last().SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pairStatus.Status, Is.EqualTo("Waiting"));
                Assert.That(pairStatus.LastError, Is.EqualTo(message));
                Assert.That(pairStatus.CurrentOperation, Is.EqualTo(message));
                Assert.That(DesktopActionRequiredMessageResolver.FromStatus(statusEvents.Last()), Is.Empty);
            });
        }

        [Test]
        public async Task LoadAsync_ReportsMissingEnabledLocalRootAsError()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore syncPairStore = new(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            syncPair.LocalRootPath = Path.Combine(_tempDirectory, "missing-root");
            await syncPairStore.UpsertAsync(syncPair);
            QueueingDesktopSyncApplicationFactory factory = new();
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            DesktopSyncPairSnapshot row = snapshot.SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(row.Status, Is.EqualTo("Error"));
                Assert.That(row.LastError, Is.EqualTo("Local folder is unavailable."));
            });
        }

        [Test]
        public async Task StatusChanged_ReportsMissingEnabledLocalRootAsError()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            SqliteSyncPairSettingsStore syncPairStore = new(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            syncPair.LocalRootPath = Path.Combine(_tempDirectory, "missing-runtime-root");
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            List<DesktopSyncStatusSnapshot> statusEvents = [];
            controller.StatusChanged += (_, status) => statusEvents.Add(status);

            await controller.LoadAsync();
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPair.Id,
                        syncPair.DisplayName,
                        SyncPairRunState.Idle,
                        null,
                        null,
                        DateTime.UtcNow),
                ],
                DateTime.UtcNow));

            DesktopSyncPairStatusSnapshot pairStatus = statusEvents.Last().SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pairStatus.Status, Is.EqualTo("Error"));
                Assert.That(pairStatus.LastError, Is.EqualTo("Local folder is unavailable."));
                Assert.That(pairStatus.CurrentOperation, Is.EqualTo("Action required: Local folder is unavailable."));
            });
        }

        [Test]
        public async Task LoadAsync_KeepsMissingDisabledLocalRootDisabled()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore syncPairStore = new(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: false);
            syncPair.LocalRootPath = Path.Combine(_tempDirectory, "disabled-missing-root");
            await syncPairStore.UpsertAsync(syncPair);
            QueueingDesktopSyncApplicationFactory factory = new();
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            DesktopSyncPairSnapshot row = snapshot.SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(row.Status, Is.EqualTo("Disabled"));
                Assert.That(row.LastError, Is.Null);
            });
        }

        [Test]
        public async Task SessionRevoked_ForwardsSessionRevocationEvents()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            List<DesktopSessionRevocationSnapshot> sessionRevocations = new List<DesktopSessionRevocationSnapshot>();
            DateTime occurredAtUtc = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
            controller.SessionRevoked += (_, sessionRevocation) => sessionRevocations.Add(sessionRevocation);

            await controller.LoadAsync();
            host.SessionRevocationPublisher.Publish(new SessionRevocationEvent(occurredAtUtc));

            Assert.Multiple(() =>
            {
                Assert.That(sessionRevocations, Has.Count.EqualTo(1));
                Assert.That(sessionRevocations[0].OccurredAtUtc, Is.EqualTo(occurredAtUtc));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsLastSessionRevocation()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            DateTime occurredAtUtc = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

            await controller.LoadAsync();
            host.SessionRevocationPublisher.Publish(new SessionRevocationEvent(occurredAtUtc));
            JsonElement auth = await ReadDiagnosticsRootAsync(controller, "auth");

            Assert.That(
                auth.GetProperty("lastSessionRevokedAtUtc").GetDateTimeOffset(),
                Is.EqualTo(new DateTimeOffset(occurredAtUtc)));
        }

        [Test]
        public async Task LoadAsync_UsesRuntimeLastSuccessfulSyncWhenBaselineIsEmpty()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            Guid syncPairId = Guid.NewGuid();
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            await syncPairStore.UpsertAsync(new SyncPairSettings
            {
                Id = syncPairId,
                DisplayName = "Empty folder",
                LocalRootPath = Path.Combine(_tempDirectory, "Empty folder"),
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Empty folder",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            DateTime completedAtUtc = new(2026, 6, 4, 9, 30, 0, DateTimeKind.Utc);
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPairId,
                        "Empty folder",
                        SyncPairRunState.Idle,
                        null,
                        null,
                        DateTime.UtcNow,
                        completedAtUtc),
                ],
                DateTime.UtcNow));
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SyncPairs.Single().LastSyncedAtUtc, Is.EqualTo(completedAtUtc));
                Assert.That(File.Exists(paths.SyncStateDatabasePath), Is.True);
            });
        }
    }
}
