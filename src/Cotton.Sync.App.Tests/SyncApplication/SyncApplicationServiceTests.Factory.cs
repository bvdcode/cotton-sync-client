// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        private static SyncApplicationService CreateService(
            ISyncPairSettingsStore store,
            ISyncPairPrerequisiteValidator? prerequisites = null,
            IAppPreferencesStore? preferences = null,
            IAuthFlow? authFlow = null,
            IAppCodeBrowserAuthFlow? appCodeBrowserAuthFlow = null,
            ISyncSupervisor? supervisor = null,
            IPlatformCommandService? platformCommands = null,
            ILocalChangeSyncCoordinator? localChanges = null,
            IRemoteChangeSyncCoordinator? remoteChanges = null,
            IPeriodicSyncCoordinator? periodicSync = null,
            IEnumerable<ISyncCoreLifecycleComponent>? syncCoreLifecycleComponents = null,
            ISyncStateStore? syncStateStore = null,
            ISyncPairDeletionHandler? syncPairDeletionHandler = null,
            SyncPairSettingsValidator? validator = null)
        {
            return new SyncApplicationService(
                store,
                prerequisites ?? new FakeSyncPairPrerequisiteValidator([]),
                preferences ?? new FakeAppPreferencesStore(),
                authFlow ?? new FakeAuthFlow(),
                appCodeBrowserAuthFlow ?? new FakeAppCodeBrowserAuthFlow(),
                supervisor ?? new FakeSyncSupervisor(),
                platformCommands ?? new FakePlatformCommandService(),
                localChanges,
                remoteChanges,
                periodicSync,
                syncCoreLifecycleComponents,
                syncStateStore,
                validator,
                syncPairDeletionHandler: syncPairDeletionHandler);
        }

        private static SyncPairSettings CreatePair(string localRootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = localRootPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
            };
        }

        private static SyncPairSettings CopySyncPair(SyncPairSettings source)
        {
            return new SyncPairSettings
            {
                Id = source.Id,
                DisplayName = source.DisplayName,
                LocalRootPath = source.LocalRootPath,
                RemoteRootNodeId = source.RemoteRootNodeId,
                RemoteDisplayPath = source.RemoteDisplayPath,
                IsEnabled = source.IsEnabled,
                Mode = source.Mode,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
            };
        }

    }
}
