// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
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
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using AppRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController : IDesktopShellController
    {
        private const string SelfTestSyncPairId = "__desktop_self_test__";
        private const string SyncCoreStateSignedOut = "signedOut";
        private const string SyncCoreStateStopped = "stopped";
        private const string SyncCoreStateStarting = "starting";
        private const string SyncCoreStateRunning = "running";
        private const string SyncCoreStateStartFailed = "startFailed";
        private const string LocalRootUnavailableError = "Local folder is unavailable.";

        private static readonly TimeSpan SavedSessionRestoreTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SavedSessionRestoreRetryBaseDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ServerProbeTimeout = TimeSpan.FromSeconds(5);
        private const int SavedSessionRestoreMaxAttempts = 3;
        private const long EmptyStateDatabaseFreelistWarningBytes = 4L * 1024 * 1024;
        private const double EmptyStateDatabaseFreelistWarningRatio = 0.50d;

        private readonly IDesktopSyncApplicationFactory _factory;
        private readonly IPlatformCommandService _platformCommands;
        private readonly IAutostartService _autostartService;
        private readonly DesktopDiagnosticsExporter _diagnosticsExporter;
        private readonly Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>> _tokenStorageVerifier;
        private readonly DesktopAppPaths _paths;
        private readonly SqliteAppPreferencesStore _preferencesStore;
        private readonly DesktopStartupOptions _startupOptions;
        private readonly TimeSpan _savedSessionRestoreTimeout;
        private readonly TimeSpan _savedSessionRestoreRetryBaseDelay;
        private readonly TimeSpan _serverProbeTimeout;
        private readonly object _progressGate = new();
        private readonly object _syncPairSettingsGate = new();
        private readonly SqliteSyncPairSettingsStore _syncPairStore;
        private readonly TimeSpan _tokenStorageVerificationTimeout;
        private readonly IDesktopUpdateService _updateService;
        private readonly IDisposable? _updateServiceLifetime;
        private readonly IDesktopUpdateInstaller _updateInstaller;
        private readonly Dictionary<Guid, DesktopRunProgressSnapshot> _aggregateRunProgress = [];
        private readonly Dictionary<TransferProgressKey, DesktopTransferProgressSnapshot> _currentTransfers = [];
        private Dictionary<Guid, (bool IsEnabled, string LocalRootPath)> _knownSyncPairSettings = [];
        private DesktopUpdateDiagnosticsSnapshot _lastUpdateDiagnostics =
            DesktopUpdateDiagnosticsSnapshot.NotChecked(DesktopAppVersion.Current);
        private IDisposable? _activitySubscription;
        private AuthSession? _activeSession;
        private DesktopSyncApplicationHost? _host;
        private IDisposable? _runProgressSubscription;
        private IDisposable? _sessionRevocationSubscription;
        private string _syncCoreState = SyncCoreStateSignedOut;
        private IDisposable? _statusSubscription;
        private IDisposable? _transferProgressSubscription;

        public DesktopShellController(
            DesktopAppPaths paths,
            IDesktopSyncApplicationFactory factory,
            SqliteAppPreferencesStore preferencesStore,
            SqliteSyncPairSettingsStore syncPairStore,
            IPlatformCommandService platformCommands,
            IAutostartService autostartService,
            DesktopShellControllerOptions? options = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
            _syncPairStore = syncPairStore ?? throw new ArgumentNullException(nameof(syncPairStore));
            _platformCommands = platformCommands ?? throw new ArgumentNullException(nameof(platformCommands));
            _autostartService = autostartService ?? throw new ArgumentNullException(nameof(autostartService));
            _diagnosticsExporter = new DesktopDiagnosticsExporter();
            DesktopShellControllerOptions resolvedOptions = options ?? new DesktopShellControllerOptions();
            _startupOptions = resolvedOptions.StartupOptions;
            _savedSessionRestoreTimeout = ResolvePositiveTimeout(
                resolvedOptions.SavedSessionRestoreTimeout,
                SavedSessionRestoreTimeout,
                nameof(resolvedOptions.SavedSessionRestoreTimeout));
            _savedSessionRestoreRetryBaseDelay = ResolveNonNegativeTimeout(
                resolvedOptions.SavedSessionRestoreRetryBaseDelay,
                SavedSessionRestoreRetryBaseDelay,
                nameof(resolvedOptions.SavedSessionRestoreRetryBaseDelay));
            _serverProbeTimeout = ResolvePositiveTimeout(
                resolvedOptions.ServerProbeTimeout,
                ServerProbeTimeout,
                nameof(resolvedOptions.ServerProbeTimeout));
            _tokenStorageVerificationTimeout = ResolvePositiveTimeout(
                resolvedOptions.TokenStorageVerificationTimeout,
                _savedSessionRestoreTimeout,
                nameof(resolvedOptions.TokenStorageVerificationTimeout));
            _tokenStorageVerifier = ResolveTokenStorageVerifier(resolvedOptions);
            (_updateService, _updateServiceLifetime) = CreateUpdateService(resolvedOptions.UpdateService, _paths);
            _updateInstaller = resolvedOptions.UpdateInstaller ?? new DesktopUpdateInstaller();
        }

        private static TimeSpan ResolvePositiveTimeout(
            TimeSpan? configured,
            TimeSpan defaultValue,
            string parameterName)
        {
            TimeSpan value = configured ?? defaultValue;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, parameterName);
            return value;
        }

        private static TimeSpan ResolveNonNegativeTimeout(
            TimeSpan? configured,
            TimeSpan defaultValue,
            string parameterName)
        {
            TimeSpan value = configured ?? defaultValue;
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, parameterName);
            return value;
        }

        private static Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>> ResolveTokenStorageVerifier(
            DesktopShellControllerOptions options)
        {
            if (options.TokenStorageVerifier is not null)
            {
                return options.TokenStorageVerifier;
            }

            if (options.TokenStorageCapabilities is not null)
            {
                return cancellationToken => Task.FromResult(options.TokenStorageCapabilities());
            }

            return DesktopTokenStorageCapabilities.CreateVerifiedSnapshotAsync;
        }

        private static (IDesktopUpdateService Service, IDisposable? Lifetime) CreateUpdateService(
            IDesktopUpdateService? configuredService,
            DesktopAppPaths paths)
        {
            if (configuredService is not null)
            {
                return (configuredService, null);
            }

            DesktopUpdateService service = new(
                DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30)),
                DesktopAppVersion.Current,
                paths.UpdateCacheDirectory,
                disposeHttpClient: true);
            return (service, service);
        }

        public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged;

        public event EventHandler<DesktopActivitySnapshot>? ActivityReported;

        public event EventHandler<DesktopSessionRevocationSnapshot>? SessionRevoked;

        public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged;

        public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged;























































































































    }
}
