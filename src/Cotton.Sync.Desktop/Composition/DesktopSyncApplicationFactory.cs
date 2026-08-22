// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using HeadlessSyncEngine = Cotton.Sync.SyncEngine;

namespace Cotton.Sync.Desktop.Composition
{
    internal class DesktopSyncApplicationFactory : IDesktopSyncApplicationFactory
    {
        private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IPlatformCommandService? _browserAuthPlatformCommands;
        private readonly Func<HttpClient> _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly DesktopAppPaths _paths;

        public DesktopSyncApplicationFactory(
            DesktopAppPaths paths,
            ILoggerFactory? loggerFactory = null,
            IPlatformCommandService? browserAuthPlatformCommands = null,
            Func<HttpClient>? httpClientFactory = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _loggerFactory = loggerFactory ?? new DesktopTraceLoggerFactory();
            _browserAuthPlatformCommands = browserAuthPlatformCommands;
            _httpClientFactory = httpClientFactory ?? (() => DesktopHttpClientFactory.Create(HttpRequestTimeout));
        }

        public DesktopSyncApplicationHost Create(Uri serverUrl)
        {
            ArgumentNullException.ThrowIfNull(serverUrl);

            HttpClient httpClient = _httpClientFactory();
            FileCottonTokenStore tokenStore = new(_paths.TokenStorePath);
            CottonSdkOptions sdkOptions = new()
            {
                BaseAddress = serverUrl,
                UserAgent = DesktopDeviceIdentity.CreateUserAgent(),
                DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
            };
            CottonCloudClient cottonClient = new(httpClient, tokenStore, sdkOptions, _loggerFactory);

            SqliteSyncPairSettingsStore syncPairStore = new(_paths.AppDatabasePath);
            SqliteAppPreferencesStore preferencesStore = new(_paths.AppDatabasePath);
            SqliteSyncStateStore stateStore = new(_paths.SyncStateDatabasePath);

            RemoteTreeCrawler remoteTreeCrawler = new(cottonClient.Nodes);
            SdkRemoteFileSynchronizer remoteFileSynchronizer = new(cottonClient);
            SdkRemoteDirectorySynchronizer remoteDirectorySynchronizer = new(cottonClient.Nodes);
            RemoteChangeFeedReader remoteChangeFeed = new(cottonClient.Sync, stateStore);
            InMemoryAppActivityPublisher activityPublisher = new();
            InMemorySessionRevocationPublisher sessionRevocationPublisher = new();
            InMemoryAppTransferProgressPublisher transferProgressPublisher = new();
            InMemoryAppRunProgressPublisher runProgressPublisher = new();
            LocalChangeSuppression localChangeSuppression = new();
            WindowsLocalProviderFileMarker localProviderFileMarker = new(
                Path.Combine(_paths.DataDirectory, "provider-file-markers"),
                _loggerFactory.CreateLogger<WindowsLocalProviderFileMarker>());
            WindowsCloudFilesNativeApi cloudFilesNativeApi = new();
            WindowsCloudFilesAdapter cloudFilesAdapter = new(nativeApi: cloudFilesNativeApi);
            WindowsCloudFilesHydrationCoordinator cloudFilesHydration = new(
                new RemoteFileRangeSynchronizerCloudFilesContentProvider(remoteFileSynchronizer),
                cloudFilesNativeApi,
                transferProgressFactory: syncPairId =>
                    new WindowsCloudFilesAppTransferProgressReporter(syncPairId, transferProgressPublisher));
            WindowsCloudFilesSyncRootConnectionCoordinator cloudFilesConnections = new(
                syncPairStore,
                cloudFilesAdapter,
                cloudFilesHydration,
                _loggerFactory.CreateLogger<WindowsCloudFilesSyncRootConnectionCoordinator>());
            WindowsCloudFilesSyncPairDeletionHandler cloudFilesDeletionHandler = new(
                cloudFilesAdapter,
                _loggerFactory.CreateLogger<WindowsCloudFilesSyncPairDeletionHandler>(),
                syncStateStore: stateStore);
            DesktopCloudFilesPlaceholderWriter remoteFilePlaceholderWriter = new(
                cloudFilesAdapter: cloudFilesAdapter,
                localChangeSuppression: localChangeSuppression,
                logger: _loggerFactory.CreateLogger<DesktopCloudFilesPlaceholderWriter>(),
                providerFileMarker: localProviderFileMarker);
            HeadlessSyncEngine syncEngine = new(
                new LocalFileScanner(),
                remoteTreeCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteDirectories: remoteDirectorySynchronizer,
                remoteFilePlaceholderWriter: remoteFilePlaceholderWriter,
                logger: _loggerFactory.CreateLogger<HeadlessSyncEngine>());
            ISyncPairWork pairWork = new WindowsVirtualFilesDehydrationPairWork(
                new RemoteChangeAwareSyncPairWork(
                    new WindowsVirtualFilesFilePlaceholderRepairPairWork(
                        new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                            new WindowsVirtualFilesUploadFinalizationPairWork(
                                new SyncEnginePairWork(syncEngine, activityPublisher, transferProgressPublisher, runProgressPublisher),
                                activityPublisher,
                                stateStore,
                                cloudFilesAdapter,
                                localChangeSuppression,
                                runProgressPublisher),
                            stateStore,
                            cloudFilesAdapter,
                            localChangeSuppression,
                            runProgressPublisher: runProgressPublisher),
                        stateStore,
                        cloudFilesAdapter,
                        localChangeSuppression,
                        runProgressPublisher: runProgressPublisher),
                    remoteChangeFeed,
                    stateStore),
                stateStore,
                cloudFilesAdapter,
                new LocalFileScanner(),
                localChangeSuppression: localChangeSuppression,
                runProgressPublisher: runProgressPublisher);
            SyncPairRunnerFactory runnerFactory = new(pairWork, loggerFactory: _loggerFactory);
            InMemoryAppStatusPublisher statusPublisher = new();
            SyncSupervisor supervisor = new(syncPairStore, runnerFactory, statusPublisher);
            LocalChangeSyncCoordinator localChanges = new(
                syncPairStore,
                supervisor,
                new FileSystemLocalSyncRootWatcherFactory(_loggerFactory),
                logger: _loggerFactory.CreateLogger<LocalChangeSyncCoordinator>(),
                changeSuppression: localChangeSuppression,
                offlineChangeDetector: new LocalOfflineChangeDetector(
                    new LocalFileScanner(),
                    stateStore,
                    localProviderFileMarker));
            PeriodicSyncCoordinator periodicSync = new(
                supervisor,
                logger: _loggerFactory.CreateLogger<PeriodicSyncCoordinator>());
            ProcessPlatformCommandService platformCommands = new(
                _loggerFactory.CreateLogger<ProcessPlatformCommandService>());
            PasswordAuthFlow authFlow = new(cottonClient.Auth);
            AppCodeBrowserAuthFlow appCodeBrowserAuthFlow = new(
                cottonClient.Auth,
                _browserAuthPlatformCommands ?? platformCommands);
            SessionRevocationHandler sessionRevocationHandler = new(
                authFlow,
                localChanges,
                periodicSync,
                supervisor,
                sessionRevocationPublisher,
                _loggerFactory.CreateLogger<SessionRevocationHandler>());
            RealtimeRemoteChangeSyncCoordinator remoteChanges = new(
                cottonClient.Realtime,
                supervisor,
                sessionRevocationHandler: sessionRevocationHandler,
                logger: _loggerFactory.CreateLogger<RealtimeRemoteChangeSyncCoordinator>());
            ISyncPairPrerequisiteValidator prerequisites = new DesktopSyncPairPrerequisiteValidator(
                new SyncPairPrerequisiteValidator(
                    new FileSystemLocalSyncRootProbe(_loggerFactory.CreateLogger<FileSystemLocalSyncRootProbe>()),
                    new SdkRemoteSyncRootProbe(
                        cottonClient.Nodes,
                        _loggerFactory.CreateLogger<SdkRemoteSyncRootProbe>())));
            SyncApplicationService appService = new(
                syncPairStore,
                prerequisites,
                preferencesStore,
                authFlow,
                appCodeBrowserAuthFlow,
                supervisor,
                platformCommands,
                localChanges,
                remoteChanges,
                periodicSync,
                syncCoreLifecycleComponents: [cloudFilesConnections],
                stateStore,
                new SyncPairSettingsValidator(DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities()),
                syncPairDeletionHandler: cloudFilesDeletionHandler,
                logger: _loggerFactory.CreateLogger<SyncApplicationService>());
            RemoteRootResolver remoteRootResolver = new(cottonClient.Nodes);

            return new DesktopSyncApplicationHost(
                appService,
                remoteRootResolver,
                statusPublisher,
                activityPublisher,
                sessionRevocationPublisher,
                transferProgressPublisher,
                runProgressPublisher,
                tokenStore,
                cottonClient.Nodes,
                cottonClient.Sync,
                httpClient,
                serverUrl,
                cottonClient,
                new DesktopCompositionSnapshot(
                    cottonClient.GetType(),
                    localChanges.GetType(),
                    remoteChanges.GetType(),
                    periodicSync.GetType(),
                    pairWork.GetType(),
                    typeof(RemoteChangeAwareSyncPairWork),
                    typeof(WindowsVirtualFilesFilePlaceholderRepairPairWork),
                    typeof(WindowsVirtualFilesDirectoryPlaceholderRepairPairWork),
                    typeof(WindowsVirtualFilesUploadFinalizationPairWork),
                    typeof(SyncEnginePairWork),
                    remoteFilePlaceholderWriter.GetType(),
                    cloudFilesConnections.GetType(),
                    cloudFilesDeletionHandler.GetType()));
        }
    }
}
