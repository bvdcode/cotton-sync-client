// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
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
    internal class DesktopShellController : IDesktopShellController
    {
        private const string SelfTestSyncPairId = "__desktop_self_test__";
        private const string SyncCoreStateSignedOut = "signedOut";
        private const string SyncCoreStateStopped = "stopped";
        private const string SyncCoreStateStarting = "starting";
        private const string SyncCoreStateRunning = "running";
        private const string SyncCoreStateStartFailed = "startFailed";

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
        private readonly SqliteSyncPairSettingsStore _syncPairStore;
        private readonly TimeSpan _tokenStorageVerificationTimeout;
        private readonly IDesktopUpdateService _updateService;
        private readonly IDisposable? _updateServiceLifetime;
        private readonly IDesktopUpdateInstaller _updateInstaller;
        private DesktopUpdateDiagnosticsSnapshot _lastUpdateDiagnostics =
            DesktopUpdateDiagnosticsSnapshot.NotChecked(DesktopAppVersion.Current);
        private IDisposable? _activitySubscription;
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
            DesktopStartupOptions? startupOptions = null,
            Func<DesktopTokenStorageCapabilitySnapshot>? tokenStorageCapabilities = null,
            Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>>? tokenStorageVerifier = null,
            TimeSpan? savedSessionRestoreTimeout = null,
            TimeSpan? savedSessionRestoreRetryBaseDelay = null,
            TimeSpan? serverProbeTimeout = null,
            TimeSpan? tokenStorageVerificationTimeout = null,
            IDesktopUpdateService? updateService = null,
            IDesktopUpdateInstaller? updateInstaller = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
            _syncPairStore = syncPairStore ?? throw new ArgumentNullException(nameof(syncPairStore));
            _platformCommands = platformCommands ?? throw new ArgumentNullException(nameof(platformCommands));
            _autostartService = autostartService ?? throw new ArgumentNullException(nameof(autostartService));
            _diagnosticsExporter = new DesktopDiagnosticsExporter();
            _startupOptions = startupOptions ?? DesktopStartupOptions.Empty;
            _savedSessionRestoreTimeout = savedSessionRestoreTimeout ?? SavedSessionRestoreTimeout;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_savedSessionRestoreTimeout, TimeSpan.Zero);
            _savedSessionRestoreRetryBaseDelay = savedSessionRestoreRetryBaseDelay ?? SavedSessionRestoreRetryBaseDelay;
            ArgumentOutOfRangeException.ThrowIfLessThan(_savedSessionRestoreRetryBaseDelay, TimeSpan.Zero);
            _serverProbeTimeout = serverProbeTimeout ?? ServerProbeTimeout;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_serverProbeTimeout, TimeSpan.Zero);
            _tokenStorageVerificationTimeout = tokenStorageVerificationTimeout ?? _savedSessionRestoreTimeout;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_tokenStorageVerificationTimeout, TimeSpan.Zero);
            _tokenStorageVerifier = tokenStorageVerifier
                ?? (tokenStorageCapabilities is null
                    ? DesktopTokenStorageCapabilities.CreateVerifiedSnapshotAsync
                    : cancellationToken => Task.FromResult(tokenStorageCapabilities()));
            _updateService = updateService
                ?? new DesktopUpdateService(
                    DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30)),
                    DesktopAppVersion.Current,
                    _paths.UpdateCacheDirectory,
                    disposeHttpClient: true);
            _updateServiceLifetime = updateService is null ? _updateService as IDisposable : null;
            _updateInstaller = updateInstaller ?? new DesktopUpdateInstaller();
        }

        public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged;

        public event EventHandler<DesktopActivitySnapshot>? ActivityReported;

        public event EventHandler<DesktopSessionRevocationSnapshot>? SessionRevoked;

        public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged;

        public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged;

        public async Task<DesktopShellSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await InitializeSyncStateStoreAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            bool startWithOperatingSystem = await ResolveStartWithOperatingSystemAsync(
                preferences,
                cancellationToken).ConfigureAwait(false);
            bool appliedAutostart = await TryApplyPreferredAutostartAsync(
                preferences,
                cancellationToken).ConfigureAwait(false);
            if (appliedAutostart)
            {
                startWithOperatingSystem = true;
                await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
            Uri? serverUrl = _startupOptions.ServerUrl ?? preferences.RememberedServerUrl;
            string? startupErrorMessage = null;
            AuthSession? session = null;
            if (serverUrl is not null)
            {
                try
                {
                    session = await TryRestoreSessionAsync(serverUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Trace.TraceWarning("Failed to restore desktop session for {0}: {1}", serverUrl, exception);
                    startupErrorMessage = DesktopActionRequiredMessageResolver.FromException(exception);
                }
            }
            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            IReadOnlyList<DesktopSyncPairSnapshot> syncPairSnapshots = await BuildSyncPairSnapshotsAsync(
                syncPairs,
                cancellationToken).ConfigureAwait(false);
            return new DesktopShellSnapshot(
                serverUrl,
                session?.Email ?? session?.Username,
                _startupOptions.Username ?? preferences.RememberedUsername,
                startWithOperatingSystem,
                preferences.EnableNotifications,
                preferences.ThemeMode,
                CreateDataPathSnapshot(),
                platformCapabilities with { IsAutostartSupported = _autostartService.IsSupported },
                session is not null,
                syncPairSnapshots,
                DesktopDeviceIdentity.CreateDeviceName(),
                startupErrorMessage);
        }

        public async Task<DesktopServerProbeResult> ProbeServerAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            Uri parsedServerUrl = ParseServerUrl(serverUrl);
            using HttpClient httpClient = DesktopHttpClientFactory.Create(_serverProbeTimeout);
            httpClient.BaseAddress = parsedServerUrl;
            PublicServerInfo? info;
            try
            {
                info = await httpClient
                    .GetFromJsonAsync<PublicServerInfo>(Routes.V1.Server + "/info", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Cotton server check timed out after "
                    + _serverProbeTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds.",
                    exception);
            }

            bool isCottonServer = string.Equals(info?.Product, Constants.ProductName, StringComparison.Ordinal);
            return new DesktopServerProbeResult(
                parsedServerUrl,
                isCottonServer,
                info?.Product,
                info?.InstanceIdHash);
        }

        public async Task<AuthSession> SignInAsync(
            DesktopSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Uri serverUrl = ParseServerUrl(request.ServerUrl);
            await EnsureReleaseSecureTokenStorageAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncApplicationHost host = _factory.Create(serverUrl);
            try
            {
                AuthSession session = await host.App.SignInAsync(
                    new PasswordSignInRequest
                    {
                        Username = request.Username.Trim(),
                        Password = request.Password,
                        TwoFactorCode = NormalizeOptional(request.TotpCode),
                        TrustDevice = true,
                    },
                    cancellationToken).ConfigureAwait(false);
                await CompleteSignInAsync(host, serverUrl, session, request.Username.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<AuthSession> SignInWithBrowserAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            Uri parsedServerUrl = ParseServerUrl(serverUrl);
            await EnsureReleaseSecureTokenStorageAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncApplicationHost host = _factory.Create(parsedServerUrl);
            try
            {
                AuthSession session = await host.App.SignInWithBrowserAsync(
                    new AppCodeBrowserSignInRequest
                    {
                        ApplicationName = "Cotton Sync Desktop",
                        ApplicationVersion = DesktopAppVersion.Current,
                        DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
                    },
                    cancellationToken).ConfigureAwait(false);
                await CompleteSignInAsync(
                        host,
                        parsedServerUrl,
                        session,
                        session.Email ?? session.Username,
                        cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<DesktopRemoteFolderListSnapshot> ListRemoteFoldersAsync(
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost host = RequireHost();
            string normalizedPath = NormalizeRemotePath(remotePath);
            NodeDto current = await host.Nodes.ResolveAsync(
                normalizedPath == "/" ? null : normalizedPath,
                cancellationToken).ConfigureAwait(false);
            NodeContentDto children = await host.Nodes.GetChildrenAsync(
                current.Id,
                page: 1,
                pageSize: 200,
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            List<DesktopRemoteFolderSnapshot> folders = children.Nodes
                .OrderBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(node => new DesktopRemoteFolderSnapshot(
                    node.Id,
                    node.Name,
                    CombineRemotePath(normalizedPath, node.Name)))
                .ToList();
            return new DesktopRemoteFolderListSnapshot(normalizedPath, folders);
        }

        public async Task<DesktopRemoteFolderSnapshot> CreateRemoteFolderAsync(
            string parentPath,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost host = RequireHost();
            string normalizedPath = NormalizeRemotePath(parentPath);
            string normalizedName = NormalizeRequired(folderName, nameof(folderName));
            if (normalizedName.Contains('/') || normalizedName.Contains('\\'))
            {
                throw new ArgumentException("Cloud folder name cannot contain path separators.", nameof(folderName));
            }

            NodeDto parent = await host.Nodes.ResolveAsync(
                normalizedPath == "/" ? null : normalizedPath,
                cancellationToken).ConfigureAwait(false);
            NodeDto created = await host.Nodes.CreateAsync(parent.Id, normalizedName, cancellationToken)
                .ConfigureAwait(false);
            return new DesktopRemoteFolderSnapshot(
                created.Id,
                created.Name,
                CombineRemotePath(normalizedPath, created.Name));
        }

        public async Task<SyncPairSettings> AddSyncPairAsync(
            DesktopSyncPairRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            DesktopSyncApplicationHost host = RequireHost();
            string localPath = NormalizeRequired(request.LocalFolderPath, nameof(request.LocalFolderPath));
            string remotePath = NormalizeRemotePath(request.RemoteFolderPath);
            NodeDto remoteRoot = await host.RemoteRootResolver.EnsureAsync(remotePath, cancellationToken).ConfigureAwait(false);
            var syncPair = new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = CreateDisplayName(localPath, remotePath, remoteRoot),
                LocalRootPath = localPath,
                RemoteRootNodeId = remoteRoot.Id,
                RemoteDisplayPath = remotePath,
                IsEnabled = true,
                Mode = request.Mode,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };

            SyncPairSaveResult result = await host.App.SaveSyncPairAsync(syncPair, cancellationToken).ConfigureAwait(false);
            if (!result.IsSaved)
            {
                throw new SyncPairValidationException(result.Validation.Errors);
            }

            StartInitialSyncInBackground(host, syncPair.Id, syncPair.LocalRootPath);
            return syncPair;
        }

        public async Task SetSyncPairEnabledAsync(
            Guid syncPairId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id is required.", nameof(syncPairId));
            }

            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncPairSettings? syncPair = await _syncPairStore.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            if (syncPair is null)
            {
                throw new InvalidOperationException("Sync pair was not found.");
            }

            if (syncPair.IsEnabled == enabled)
            {
                return;
            }

            syncPair.IsEnabled = enabled;
            syncPair.UpdatedAtUtc = DateTime.UtcNow;
            await SaveSyncPairSettingsAsync(syncPair, cancellationToken).ConfigureAwait(false);
        }

        public async Task RenameSyncPairAsync(
            Guid syncPairId,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id is required.", nameof(syncPairId));
            }

            string normalizedDisplayName = NormalizeRequired(displayName, nameof(displayName));
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncPairSettings? syncPair = await _syncPairStore.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            if (syncPair is null)
            {
                throw new InvalidOperationException("Sync pair was not found.");
            }

            if (string.Equals(syncPair.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
            {
                return;
            }

            syncPair.DisplayName = normalizedDisplayName;
            syncPair.UpdatedAtUtc = DateTime.UtcNow;
            await SaveSyncPairSettingsAsync(syncPair, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetSyncPairLocalFolderAsync(
            Guid syncPairId,
            string localFolderPath,
            CancellationToken cancellationToken = default)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id is required.", nameof(syncPairId));
            }

            string normalizedLocalPath = NormalizeRequired(localFolderPath, nameof(localFolderPath));
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncPairSettings? syncPair = await _syncPairStore.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            if (syncPair is null)
            {
                throw new InvalidOperationException("Sync pair was not found.");
            }

            if (string.Equals(syncPair.LocalRootPath, normalizedLocalPath, StringComparison.Ordinal))
            {
                return;
            }

            syncPair.LocalRootPath = normalizedLocalPath;
            syncPair.UpdatedAtUtc = DateTime.UtcNow;
            await SaveSyncPairSettingsAsync(syncPair, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SyncPairSettings> SetSyncPairRemoteFolderAsync(
            Guid syncPairId,
            string remoteFolderPath,
            CancellationToken cancellationToken = default)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id is required.", nameof(syncPairId));
            }

            DesktopSyncApplicationHost host = RequireHost();
            string normalizedRemotePath = NormalizeRemotePath(remoteFolderPath);
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncPairSettings? syncPair = await _syncPairStore.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            if (syncPair is null)
            {
                throw new InvalidOperationException("Sync pair was not found.");
            }

            if (string.Equals(syncPair.RemoteDisplayPath, normalizedRemotePath, StringComparison.Ordinal))
            {
                return syncPair;
            }

            NodeDto remoteRoot = await host.RemoteRootResolver
                .EnsureAsync(normalizedRemotePath, cancellationToken)
                .ConfigureAwait(false);
            syncPair.RemoteRootNodeId = remoteRoot.Id;
            syncPair.RemoteDisplayPath = normalizedRemotePath;
            syncPair.UpdatedAtUtc = DateTime.UtcNow;
            await SaveSyncPairSettingsAsync(syncPair, cancellationToken).ConfigureAwait(false);
            return syncPair;
        }

        public async Task RemoveSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id is required.", nameof(syncPairId));
            }

            DesktopSyncApplicationHost? host = _host;
            if (host is null)
            {
                await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await _syncPairStore.DeleteAsync(syncPairId, cancellationToken).ConfigureAwait(false);
                var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPairId.ToString(), cancellationToken).ConfigureAwait(false);
                return;
            }

            await host.App.DeleteSyncPairAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            await UpdateSyncCoreStateAfterSyncPairDeletionAsync(host, cancellationToken).ConfigureAwait(false);
        }

        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is null)
            {
                return;
            }

            try
            {
                await host.App.SignOutAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                    _syncCoreState = SyncCoreStateSignedOut;
                    _activitySubscription?.Dispose();
                    _activitySubscription = null;
                    _statusSubscription?.Dispose();
                    _statusSubscription = null;
                    _transferProgressSubscription?.Dispose();
                    _transferProgressSubscription = null;
                    _runProgressSubscription?.Dispose();
                    _runProgressSubscription = null;
                }

                await host.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task CompleteSignInAsync(
            DesktopSyncApplicationHost host,
            Uri serverUrl,
            AuthSession session,
            string rememberedUsername,
            CancellationToken cancellationToken)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.RememberedServerUrl = serverUrl;
            preferences.RememberedUsername = rememberedUsername;
            await TryApplyPreferredAutostartAsync(preferences, cancellationToken).ConfigureAwait(false);
            await host.App.SavePreferencesAsync(preferences, cancellationToken).ConfigureAwait(false);
            await ReplaceHostAsync(host, cancellationToken).ConfigureAwait(false);
            StartSessionSyncInBackground(host, "sign-in");
        }

        public Task SyncAllAsync(CancellationToken cancellationToken = default)
        {
            return RequireHost().App.SyncAllAsync(cancellationToken);
        }

        public Task PauseAllAsync(CancellationToken cancellationToken = default)
        {
            return RequireHost().App.PauseAllAsync(cancellationToken);
        }

        public Task ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            return RequireHost().App.ResumeAllAsync(cancellationToken);
        }

        public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
        {
            return _platformCommands.OpenFolderAsync(localPath, cancellationToken);
        }

        public async Task OpenWebAsync(CancellationToken cancellationToken = default)
        {
            Uri? serverUrl = _host?.ServerUrl;
            if (serverUrl is null)
            {
                await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
                serverUrl = _startupOptions.ServerUrl ?? preferences.RememberedServerUrl;
            }

            if (serverUrl is null)
            {
                throw new InvalidOperationException("Sign in before opening Cotton Cloud.");
            }

            await _platformCommands.OpenWebAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetStartWithOperatingSystemAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (enabled && !_autostartService.IsSupported)
            {
                throw new NotSupportedException("Autostart is not supported on this platform yet.");
            }

            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _autostartService.SetEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.StartWithOperatingSystem = enabled;
            preferences.StartMinimizedToTray = enabled && DesktopPlatformCapabilities.IsTrayLifecycleSupported;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> ResolveStartWithOperatingSystemAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken)
        {
            bool isEnabled = await TryReadStartWithOperatingSystemAsync(cancellationToken).ConfigureAwait(false);
            if (isEnabled)
            {
                return true;
            }

            return preferences.StartWithOperatingSystem && _autostartService.IsSupported;
        }

        private async Task<bool> TryReadStartWithOperatingSystemAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceError("Failed to read Cotton Sync autostart state. {0}", exception);
                return false;
            }
        }

        private async Task<bool> TryApplyPreferredAutostartAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken)
        {
            if (!preferences.StartWithOperatingSystem || !_autostartService.IsSupported)
            {
                return false;
            }

            try
            {
                bool isEnabled = await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
                if (!isEnabled)
                {
                    await _autostartService.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                }

                preferences.StartMinimizedToTray = DesktopPlatformCapabilities.IsTrayLifecycleSupported;
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceError("Failed to apply Cotton Sync autostart preference. {0}", exception);
                return false;
            }
        }

        public async Task SetNotificationsEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.EnableNotifications = enabled;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetThemeModeAsync(AppThemeMode themeMode, CancellationToken cancellationToken = default)
        {
            ValidateThemeMode(themeMode);
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.ThemeMode = themeMode;
            await _preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        public async Task<DesktopSelfTestSnapshot> RunSelfTestAsync(CancellationToken cancellationToken = default)
        {
            var items = new List<DesktopSelfTestItemSnapshot>();
            AppPreferences? preferences = null;
            IReadOnlyList<SyncPairSettings> syncPairs = [];

            await AddSelfTestCheckAsync(
                items,
                "Preferences database",
                async () =>
                {
                    await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
                    return "Ready";
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                items,
                "Sync pair database",
                async () =>
                {
                    await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    syncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
                    return syncPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sync pair(s)";
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                items,
                "Sync state database",
                async () =>
                {
                    var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
                    await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    await stateStore.GetChangeCursorAsync(SelfTestSyncPairId, cancellationToken).ConfigureAwait(false);
                    SyncStateStoreDiagnostics diagnostics = await stateStore.GetDiagnosticsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return CreateSyncStateDatabaseDetails(diagnostics);
                }).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                items,
                "Authentication state",
                () => CheckAuthenticationStateAsync(cancellationToken)).ConfigureAwait(false);

            DesktopTokenStorageCapabilitySnapshot tokenStorage = await _tokenStorageVerifier(cancellationToken)
                .ConfigureAwait(false);
            items.Add(new DesktopSelfTestItemSnapshot(
                "Token storage",
                tokenStorage.IsReleaseSecure,
                tokenStorage.IsReleaseSecure
                    ? tokenStorage.Details
                    : tokenStorage.Details + " (not release secure)"));

            await AddSelfTestCheckAsync(
                items,
                "Desktop icon",
                () => CheckDesktopIconAsync(cancellationToken)).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                items,
                "Update cache",
                () => CheckUpdateCacheAsync(_paths.UpdateCacheDirectory, cancellationToken)).ConfigureAwait(false);

            await AddSelfTestCheckAsync(
                items,
                "Autostart adapter",
                async () =>
                {
                    bool isEnabled = await _autostartService.IsEnabledAsync(cancellationToken).ConfigureAwait(false);
                    return isEnabled ? "Enabled" : "Disabled";
                }).ConfigureAwait(false);

            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Desktop platform",
                true,
                platformCapabilities.OperatingSystemName
                    + "; session: "
                    + platformCapabilities.DesktopSession
                    + "; desktop: "
                    + platformCapabilities.CurrentDesktop));

            items.Add(new DesktopSelfTestItemSnapshot(
                "Tray lifecycle",
                true,
                platformCapabilities.TrayLifecycleDetails));

            DesktopCloudFilesSelfTestCapabilitySnapshot modeCapabilities =
                DesktopCloudFilesCapabilities.CreateSelfTestCapability();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Windows virtual files",
                modeCapabilities.Passed,
                modeCapabilities.Details,
                Skipped: modeCapabilities.Skipped));

            DesktopNotificationCapabilitySnapshot notificationCapabilities =
                DesktopNotificationServiceFactory.CreateSelfTestCapabilitySnapshot();
            items.Add(CreateNotificationSelfTestItem(notificationCapabilities));

            await AddSelfTestCheckAsync(
                items,
                "File watcher",
                () => CheckFileWatcherAsync(cancellationToken)).ConfigureAwait(false);

            Uri? serverUrl = _startupOptions.ServerUrl ?? preferences?.RememberedServerUrl;
            if (serverUrl is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot("Server identity", true, "Not configured"));
            }
            else
            {
                await AddSelfTestCheckAsync(
                    items,
                    "Server identity",
                    async () =>
                    {
                        DesktopServerProbeResult result = await ProbeServerAsync(
                            serverUrl.AbsoluteUri,
                            cancellationToken).ConfigureAwait(false);
                        if (!result.IsCottonServer)
                        {
                            throw new InvalidOperationException("Cotton server not found.");
                        }

                        return result.Product ?? "Cotton Cloud";
                    }).ConfigureAwait(false);
            }

            DesktopSyncApplicationHost? activeHost = _host;
            if (serverUrl is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot(
                    "Desktop sync change feed",
                    false,
                    "Not configured",
                    Skipped: true));
            }
            else if (activeHost is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot(
                    "Desktop sync change feed",
                    false,
                    "Sign in to verify",
                    Skipped: true));
            }
            else
            {
                await AddSelfTestCheckAsync(
                    items,
                    "Desktop sync change feed",
                    () => CheckSyncChangeFeedAsync(activeHost, cancellationToken)).ConfigureAwait(false);
            }

            foreach (SyncPairSettings syncPair in syncPairs)
            {
                await AddSelfTestCheckAsync(
                    items,
                    "Local root: " + syncPair.DisplayName,
                    () => CheckLocalRootAsync(syncPair, cancellationToken)).ConfigureAwait(false);
                DesktopSyncApplicationHost? host = _host;
                if (host is null)
                {
                    items.Add(new DesktopSelfTestItemSnapshot(
                        "Remote root: " + syncPair.DisplayName,
                        false,
                        "Sign in to verify",
                        Skipped: true));
                }
                else
                {
                    await AddSelfTestCheckAsync(
                        items,
                        "Remote root: " + syncPair.DisplayName,
                        () => CheckRemoteRootAsync(host, syncPair, cancellationToken)).ConfigureAwait(false);
                }
            }

            return new DesktopSelfTestSnapshot(items);
        }

        public async Task<string> ExportDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            return await ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> ExportDiagnosticsAsync(
            DesktopDiagnosticsExportOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _syncPairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairStore.ListAsync(cancellationToken).ConfigureAwait(false);
            SyncStateStoreDiagnostics syncStateDiagnostics = await CreateSyncStateDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            DesktopCloudFilesRegistrationDiagnosticsSnapshot cloudFilesRegistration =
                DesktopCloudFilesRegistrationDiagnosticsSnapshot.Create(syncPairs);
            DesktopNotificationCapabilitySnapshot notificationCapabilities =
                DesktopNotificationServiceFactory.CreateSelfTestCapabilitySnapshot();
            IReadOnlyList<DesktopSelfTestItemSnapshot> diagnosticsItems =
                await CreateDiagnosticsExportItemsAsync(
                    syncPairs,
                    syncStateDiagnostics,
                    cloudFilesRegistration,
                    notificationCapabilities,
                    cancellationToken).ConfigureAwait(false);
            var bundle = new DesktopDiagnosticsBundle(
                DateTimeOffset.UtcNow,
                DesktopAppVersion.Current,
                (_startupOptions.ServerUrl ?? preferences.RememberedServerUrl)?.AbsoluteUri,
                _host is null ? "Signed out" : preferences.RememberedUsername ?? "Signed in",
                CreateDataPathSnapshot(),
                await BuildSyncPairSnapshotsAsync(syncPairs, cancellationToken).ConfigureAwait(false),
                syncStateDiagnostics,
                CreateRuntimeHealthSnapshot(),
                CreateSyncLifecycleDiagnosticsSnapshot(syncPairs),
                CreateAuthDiagnosticsSnapshot(),
                DesktopNotificationDiagnosticsSnapshot.FromCapability(notificationCapabilities),
                CreateUpdateDiagnosticsSnapshot(),
                cloudFilesRegistration,
                diagnosticsItems,
                WindowsCloudFilesDiagnostics.Shared.Snapshot());
            return await _diagnosticsExporter.ExportAsync(_paths, bundle, options, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<DesktopSelfTestItemSnapshot>> CreateDiagnosticsExportItemsAsync(
            IReadOnlyList<SyncPairSettings> syncPairs,
            SyncStateStoreDiagnostics syncStateDiagnostics,
            DesktopCloudFilesRegistrationDiagnosticsSnapshot cloudFilesRegistration,
            DesktopNotificationCapabilitySnapshot notificationCapabilities,
            CancellationToken cancellationToken)
        {
            var items = new List<DesktopSelfTestItemSnapshot>
            {
                new(
                    "Diagnostics export",
                    true,
                    "Captured current diagnostics and read-only capability checks; self-test probes were not run."),
                new(
                    "Sync pair database",
                    true,
                    syncPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sync pair(s)"),
                CreateSyncStateDiagnosticsItem(syncStateDiagnostics),
            };

            await AddSelfTestCheckAsync(
                items,
                "Authentication state",
                () => CheckAuthenticationStateAsync(cancellationToken)).ConfigureAwait(false);

            DesktopTokenStorageCapabilitySnapshot tokenStorage = DesktopTokenStorageCapabilities.CreateSnapshot();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Token storage",
                tokenStorage.IsReleaseSecure,
                tokenStorage.IsReleaseSecure
                    ? tokenStorage.Details
                    : tokenStorage.Details + " (not release secure)"));

            DesktopPlatformCapabilitySnapshot platformCapabilities = DesktopPlatformCapabilities.CreateSnapshot();
            items.Add(new DesktopSelfTestItemSnapshot(
                "Desktop platform",
                true,
                platformCapabilities.OperatingSystemName
                    + "; session: "
                    + platformCapabilities.DesktopSession
                    + "; desktop: "
                    + platformCapabilities.CurrentDesktop));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Tray lifecycle",
                true,
                platformCapabilities.TrayLifecycleDetails));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Windows virtual files",
                platformCapabilities.IsWindowsVirtualFilesSupported,
                "Read-only capability check: "
                    + platformCapabilities.WindowsVirtualFilesDetails
                    + " Full Cloud Files connection self-test was not run during diagnostics export.",
                Skipped: !platformCapabilities.IsWindowsVirtualFilesSupported));
            items.Add(CreateNotificationSelfTestItem(notificationCapabilities));
            items.Add(new DesktopSelfTestItemSnapshot(
                "Cloud Files registration",
                cloudFilesRegistration.MissingSyncPairCount == 0 && cloudFilesRegistration.UnknownSyncPairCount == 0,
                CreateCloudFilesRegistrationDetails(cloudFilesRegistration),
                Skipped: cloudFilesRegistration.VirtualFilesSyncPairCount == 0));

            return items;
        }

        public async Task<DesktopUpdateStatusSnapshot> CheckForUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Manual,
            CancellationToken cancellationToken = default)
        {
            string sourceName = FormatUpdateCheckSource(source);
            try
            {
                Trace.TraceInformation("Starting desktop update check: source={0}, currentVersion={1}.", sourceName, DesktopAppVersion.Current);
                DesktopUpdateCheckResult check = await _updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
                RecordUpdateCheckSuccess(sourceName, check, installerPath: null);
                Trace.TraceInformation(
                    "Desktop update check completed: source={0}, currentVersion={1}, latestVersion={2}, updateAvailable={3}, installerAsset={4}.",
                    sourceName,
                    check.CurrentVersion,
                    check.LatestVersion,
                    check.IsUpdateAvailable,
                    check.InstallerAsset?.Name ?? "none");
                return ToUpdateStatus(check, installerPath: null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordUpdateCheckFailure(sourceName, exception);
                Trace.TraceWarning("Desktop update check failed: source={0}, error={1}.", sourceName, exception);
                throw;
            }
        }

        public async Task<DesktopUpdateStatusSnapshot> DownloadUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Download,
            IProgress<DesktopUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string sourceName = FormatUpdateCheckSource(source);
            try
            {
                Trace.TraceInformation("Starting desktop update download flow: source={0}, currentVersion={1}.", sourceName, DesktopAppVersion.Current);
                DesktopUpdateCheckResult check = await _updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
                if (!check.IsUpdateAvailable || check.InstallerAsset is null)
                {
                    RecordUpdateCheckSuccess(sourceName, check, installerPath: null);
                    return ToUpdateStatus(check, installerPath: null);
                }

                DesktopUpdateDownloadResult download = await _updateService
                    .DownloadInstallerAsync(check, progress, cancellationToken)
                    .ConfigureAwait(false);
                new DesktopPendingUpdateStore(_paths.UpdateCacheDirectory).Save(new DesktopPendingUpdate(
                    check.LatestVersion.ToString(),
                    download.FilePath,
                    download.Sha256,
                    download.SizeBytes,
                    DateTime.UtcNow));
                RecordUpdateCheckSuccess(sourceName, check, download.FilePath);
                Trace.TraceInformation(
                    "Desktop update download completed: source={0}, latestVersion={1}, installerAsset={2}, sizeBytes={3}.",
                    sourceName,
                    check.LatestVersion,
                    download.InstallerAsset.Name,
                    download.SizeBytes);
                return ToUpdateStatus(check, download.FilePath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordUpdateCheckFailure(sourceName, exception);
                Trace.TraceWarning("Desktop update download flow failed: source={0}, error={1}.", sourceName, exception);
                throw;
            }
        }

        public Task<DesktopUpdateInstallResult> InstallDownloadedUpdateAsync(
            string installerPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.TraceInformation("Starting desktop update installer launch.");
            try
            {
                DesktopUpdateInstallResult result = _updateInstaller.StartSilentInstall(installerPath, launchAfterUpdate: true);
                _lastUpdateDiagnostics = _lastUpdateDiagnostics.WithInstallLaunch(result, DateTimeOffset.UtcNow);
                Trace.TraceInformation(
                    "Desktop update installer launch completed: processId={0}, exitedDuringStartupProbe={1}, exitCode={2}.",
                    result.ProcessId,
                    result.ExitedDuringStartupProbe,
                    result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
                return Task.FromResult(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _lastUpdateDiagnostics = _lastUpdateDiagnostics.WithInstallLaunchFailure(exception, DateTimeOffset.UtcNow);
                Trace.TraceWarning("Desktop update installer launch failed: error={0}.", exception);
                throw;
            }
        }

        public void Dispose()
        {
            DesktopSyncApplicationHost? host = DetachHost();
            if (host is not null)
            {
                StopAndDisposeHostAsync(host).GetAwaiter().GetResult();
            }

            _updateServiceLifetime?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            DesktopSyncApplicationHost? host = DetachHost();
            if (host is not null)
            {
                await StopAndDisposeHostAsync(host).ConfigureAwait(false);
            }

            _updateServiceLifetime?.Dispose();
        }

        public static DesktopShellController CreateDefault(DesktopStartupOptions? startupOptions = null)
        {
            return CreateDefault(DesktopAppPaths.CreateDefault(), startupOptions);
        }

        public static DesktopShellController CreateDefault(
            DesktopAppPaths paths,
            DesktopStartupOptions? startupOptions = null)
        {
            ArgumentNullException.ThrowIfNull(paths);
            var loggerFactory = new DesktopTraceLoggerFactory();
            return new DesktopShellController(
                paths,
                new DesktopSyncApplicationFactory(paths, loggerFactory),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                new ProcessPlatformCommandService(loggerFactory.CreateLogger<ProcessPlatformCommandService>()),
                DesktopAutostartServiceFactory.CreateDefault(),
                startupOptions);
        }

        private async Task InitializeSyncStateStoreAsync(CancellationToken cancellationToken)
        {
            var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<SyncStateStoreDiagnostics> CreateSyncStateDiagnosticsAsync(CancellationToken cancellationToken)
        {
            var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await stateStore.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }

        private DesktopUpdateDiagnosticsSnapshot CreateUpdateDiagnosticsSnapshot()
        {
            DesktopPendingUpdate? pendingUpdate =
                new DesktopPendingUpdateStore(_paths.UpdateCacheDirectory).TryLoad();
            return _lastUpdateDiagnostics with
            {
                IsUpdateCacheDirectoryPresent = Directory.Exists(_paths.UpdateCacheDirectory),
                HasPendingUpdate = pendingUpdate is not null,
                PendingVersion = pendingUpdate?.Version,
                PendingInstallerSizeBytes = pendingUpdate?.SizeBytes,
            };
        }

        private static DesktopAuthDiagnosticsSnapshot CreateAuthDiagnosticsSnapshot()
        {
            return DesktopAuthDiagnosticsState.Snapshot();
        }

        private void RecordUpdateCheckSuccess(
            string source,
            DesktopUpdateCheckResult check,
            string? installerPath)
        {
            _lastUpdateDiagnostics = DesktopUpdateDiagnosticsSnapshot.FromCheck(
                source,
                check,
                installerPath,
                DateTimeOffset.UtcNow);
        }

        private void RecordUpdateCheckFailure(
            string source,
            Exception exception)
        {
            _lastUpdateDiagnostics = DesktopUpdateDiagnosticsSnapshot.FromFailure(
                source,
                DesktopAppVersion.Current,
                exception,
                DateTimeOffset.UtcNow);
        }

        private static Task<string> CheckLocalRootAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(syncPair.LocalRootPath))
            {
                throw new DirectoryNotFoundException("Local root does not exist: " + syncPair.LocalRootPath);
            }

            _ = Directory.EnumerateFileSystemEntries(syncPair.LocalRootPath).Take(1).ToList();
            return Task.FromResult(syncPair.LocalRootPath);
        }

        private DesktopDataPathSnapshot CreateDataPathSnapshot()
        {
            return new DesktopDataPathSnapshot(
                _paths.DataDirectory,
                _paths.AppDatabasePath,
                _paths.SyncStateDatabasePath,
                _paths.TokenStorePath);
        }

        private static DesktopSelfTestItemSnapshot CreateNotificationSelfTestItem(
            DesktopNotificationCapabilitySnapshot notificationCapabilities)
        {
            ArgumentNullException.ThrowIfNull(notificationCapabilities);
            return new DesktopSelfTestItemSnapshot(
                "Notification adapter",
                notificationCapabilities.SelfTestPassed,
                notificationCapabilities.Details,
                Skipped: notificationCapabilities.SelfTestSkipped);
        }

        private static DesktopSelfTestItemSnapshot CreateSyncStateDiagnosticsItem(
            SyncStateStoreDiagnostics diagnostics)
        {
            try
            {
                return new DesktopSelfTestItemSnapshot(
                    "Sync state database",
                    true,
                    CreateSyncStateDatabaseDetails(diagnostics));
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                return new DesktopSelfTestItemSnapshot(
                    "Sync state database",
                    false,
                    DesktopActionRequiredMessageResolver.FromException(exception));
            }
        }

        private static string CreateCloudFilesRegistrationDetails(
            DesktopCloudFilesRegistrationDiagnosticsSnapshot diagnostics)
        {
            return "virtual pairs="
                + diagnostics.VirtualFilesSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", registered="
                + diagnostics.RegisteredSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", missing="
                + diagnostics.MissingSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", unknown="
                + diagnostics.UnknownSyncPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string CreateSyncStateDatabaseDetails(SyncStateStoreDiagnostics diagnostics)
        {
            if (IsEmptyBloatedStateDatabase(diagnostics))
            {
                throw new InvalidOperationException(
                    "State database has no sync entries or change cursors, but still reserves "
                    + FormatBytes(diagnostics.FileSizeBytes)
                    + " with "
                    + FormatBytes(diagnostics.FreelistBytes)
                    + " free SQLite pages. Database maintenance is required.");
            }

            return "Ready: entries="
                + diagnostics.SyncEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", cursors="
                + diagnostics.SyncChangeCursorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", file="
                + FormatBytes(diagnostics.FileSizeBytes)
                + ", used="
                + FormatBytes(diagnostics.UsedBytes)
                + ", free="
                + FormatBytes(diagnostics.FreelistBytes);
        }

        private static bool IsEmptyBloatedStateDatabase(SyncStateStoreDiagnostics diagnostics)
        {
            return !diagnostics.HasRows
                && diagnostics.FreelistBytes >= EmptyStateDatabaseFreelistWarningBytes
                && diagnostics.FreelistRatio >= EmptyStateDatabaseFreelistWarningRatio;
        }

        private static DesktopRuntimeHealthSnapshot CreateRuntimeHealthSnapshot()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new DesktopRuntimeHealthSnapshot(
                process.Id,
                process.ProcessName,
                process.WorkingSet64,
                TryReadInt64(() => process.PrivateMemorySize64),
                TryReadInt32(() => process.Threads.Count),
                TryReadInt32(() => process.HandleCount));
        }

        private DesktopSyncLifecycleDiagnosticsSnapshot CreateSyncLifecycleDiagnosticsSnapshot(
            IReadOnlyList<SyncPairSettings> syncPairs)
        {
            DesktopSyncApplicationHost? host = _host;
            bool isSignedIn = host is not null;
            string syncCoreState = isSignedIn ? _syncCoreState : SyncCoreStateSignedOut;
            bool isBackgroundActive = isSignedIn
                && (string.Equals(syncCoreState, SyncCoreStateStarting, StringComparison.Ordinal)
                    || string.Equals(syncCoreState, SyncCoreStateRunning, StringComparison.Ordinal));
            int enabledSyncPairCount = syncPairs.Count(static syncPair => syncPair.IsEnabled);
            bool hasNoSyncPairs = syncPairs.Count == 0;
            bool isZeroPairBackgroundActive = hasNoSyncPairs && isBackgroundActive;
            string status;
            string details;
            if (!isSignedIn)
            {
                status = "signedOut";
                details = "Signed out; sync background is not running.";
            }
            else if (isZeroPairBackgroundActive)
            {
                status = "zeroPairBackgroundActive";
                details = "Signed in with no configured sync pairs; sync background is active.";
            }
            else if (hasNoSyncPairs)
            {
                status = "zeroPairBackgroundInactive";
                details = "Signed in with no configured sync pairs; sync background is not active.";
            }
            else
            {
                status = "configuredPairs";
                details = "Signed in with configured sync pairs.";
            }

            return new DesktopSyncLifecycleDiagnosticsSnapshot(
                isSignedIn,
                syncCoreState,
                isBackgroundActive,
                syncPairs.Count,
                enabledSyncPairCount,
                hasNoSyncPairs,
                isZeroPairBackgroundActive,
                status,
                details);
        }

        private static long? TryReadInt64(Func<long> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static int? TryReadInt32(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = Math.Max(0, bytes);
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            string format = unitIndex == 0 || value >= 10 ? "0" : "0.0";
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        private static DesktopUpdateStatusSnapshot ToUpdateStatus(
            DesktopUpdateCheckResult check,
            string? installerPath)
        {
            string current = check.CurrentVersion.ToString();
            string latest = check.LatestVersion.ToString();
            bool installerReady = !string.IsNullOrWhiteSpace(installerPath);
            string details;
            if (!check.IsUpdateAvailable)
            {
                details = "Cotton Sync is up to date.";
            }
            else if (installerReady)
            {
                details = "Update " + latest
                    + " is ready. Click Update to install it now, or it will install automatically on next app start.";
            }
            else if (check.InstallerAsset is null)
            {
                details = "Update " + latest + " is available, but no Windows installer asset was found.";
            }
            else
            {
                details = "Update " + latest + " is available.";
            }

            return new DesktopUpdateStatusSnapshot(
                current,
                latest,
                check.IsUpdateAvailable,
                installerReady,
                details,
                installerPath,
                check.Manifest.ReleaseUrl);
        }

        private static string FormatUpdateCheckSource(DesktopUpdateCheckSource source)
        {
            return source switch
            {
                DesktopUpdateCheckSource.Manual => "manual",
                DesktopUpdateCheckSource.Periodic => "periodic",
                DesktopUpdateCheckSource.Startup => "startup",
                DesktopUpdateCheckSource.Download => "download",
                _ => "unknown",
            };
        }

        private async Task<string> CheckAuthenticationStateAsync(CancellationToken cancellationToken)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is not null)
            {
                var activeTokens = await host.TokenStore.GetAsync(cancellationToken).ConfigureAwait(false);
                if (activeTokens is null)
                {
                    throw new InvalidOperationException("Signed in session has no stored token pair.");
                }

                return "Signed in";
            }

            var tokenStore = new FileCottonTokenStore(_paths.TokenStorePath);
            var storedTokens = await tokenStore.GetAsync(cancellationToken).ConfigureAwait(false);
            return storedTokens is null ? "Signed out" : "Stored session available";
        }

        private static Task<string> CheckFileWatcherAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(Path.GetTempPath(), "cotton-sync-watcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                return Task.FromResult("Available");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static Task<string> CheckDesktopIconAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-192.png");
            if (!File.Exists(iconPath))
            {
                throw new FileNotFoundException("Desktop icon asset was not found.", iconPath);
            }

            return Task.FromResult(iconPath);
        }

        private static async Task<string> CheckUpdateCacheAsync(
            string updateCacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(updateCacheDirectory);
            string probePath = Path.Combine(updateCacheDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probePath, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probePath);
            return updateCacheDirectory;
        }

        private static async Task<string> CheckSyncChangeFeedAsync(
            DesktopSyncApplicationHost host,
            CancellationToken cancellationToken)
        {
            var response = await host.Sync.GetChangesAsync(sinceCursor: 0, limit: 1, cancellationToken)
                .ConfigureAwait(false);
            return "Ready; next cursor " + response.NextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<string> CheckRemoteRootAsync(
            DesktopSyncApplicationHost host,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            _ = await host.Nodes.GetAsync(syncPair.RemoteRootNodeId, cancellationToken).ConfigureAwait(false);
            return syncPair.RemoteRootNodeId.ToString();
        }

        private static async Task AddSelfTestCheckAsync(
            List<DesktopSelfTestItemSnapshot> items,
            string name,
            Func<Task<string>> checkAsync)
        {
            try
            {
                string details = await checkAsync().ConfigureAwait(false);
                items.Add(new DesktopSelfTestItemSnapshot(name, true, details));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Desktop self-test check failed for {0}: {1}", name, exception);
                items.Add(new DesktopSelfTestItemSnapshot(
                    name,
                    false,
                    DesktopActionRequiredMessageResolver.FromException(exception)));
            }
        }

        private async Task SaveSyncPairSettingsAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is null)
            {
                await _syncPairStore.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                return;
            }

            SyncPairSaveResult result = await host.App
                .SaveSyncPairAsync(syncPair, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSaved)
            {
                throw new SyncPairValidationException(result.Validation.Errors);
            }
        }

        private async Task<IReadOnlyList<DesktopSyncPairSnapshot>> BuildSyncPairSnapshotsAsync(
            IReadOnlyList<SyncPairSettings> settings,
            CancellationToken cancellationToken)
        {
            if (settings.Count == 0)
            {
                return [];
            }

            var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncAppStatus? currentStatus = _host?.StatusPublisher.Current;
            var snapshots = new List<DesktopSyncPairSnapshot>(settings.Count);
            foreach (SyncPairSettings syncPair in settings)
            {
                string syncPairId = syncPair.Id.ToString();
                DateTime? persistedLastSyncedAtUtc = await stateStore
                    .GetPairLastSyncedAtUtcAsync(syncPairId, cancellationToken)
                    .ConfigureAwait(false);
                SyncChangeCursor cursor = await stateStore
                    .GetChangeCursorAsync(syncPairId, cancellationToken)
                    .ConfigureAwait(false);
                SyncPairStatus? status = currentStatus?.SyncPairs
                    .FirstOrDefault(pair => pair.SyncPairId == syncPair.Id);
                snapshots.Add(ToSnapshot(syncPair, persistedLastSyncedAtUtc, cursor, status));
            }

            return snapshots;
        }

        private static DesktopSyncPairSnapshot ToSnapshot(
            SyncPairSettings settings,
            DateTime? persistedLastSyncedAtUtc = null,
            SyncChangeCursor? cursor = null,
            SyncPairStatus? status = null)
        {
            DateTime? lastSyncedAtUtc = status?.LastSuccessfulSyncAtUtc;
            lastSyncedAtUtc ??= persistedLastSyncedAtUtc;
            return new DesktopSyncPairSnapshot(
                settings.Id,
                settings.DisplayName,
                settings.LocalRootPath,
                settings.RemoteDisplayPath,
                status is null ? settings.IsEnabled ? "Idle" : "Disabled" : ToStatusText(status),
                settings.RemoteRootNodeId,
                lastSyncedAtUtc,
                cursor?.LastCursor,
                status?.LastError,
                settings.Mode);
        }

        private static Uri ParseServerUrl(string serverUrl)
        {
            return DesktopServerUrl.NormalizeRequired(serverUrl, nameof(serverUrl));
        }

        private static string NormalizeRemotePath(string remotePath)
        {
            string normalized = NormalizeRequired(remotePath, nameof(remotePath)).Replace('\\', '/').Trim();
            normalized = normalized.Trim('/');
            return normalized.Length == 0 ? "/" : "/" + normalized;
        }

        private static string CombineRemotePath(string parentPath, string folderName)
        {
            string normalizedName = NormalizeRequired(folderName, nameof(folderName)).Trim('/');
            return parentPath == "/" ? "/" + normalizedName : parentPath + "/" + normalizedName;
        }

        private static string NormalizeRequired(string value, string parameterName)
        {
            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Value is required.", parameterName);
            }

            return normalized;
        }

        private static string? NormalizeOptional(string? value)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        private static void ValidateThemeMode(AppThemeMode themeMode)
        {
            if (!Enum.IsDefined(themeMode))
            {
                throw new ArgumentOutOfRangeException(nameof(themeMode), themeMode, "Unsupported desktop theme mode.");
            }
        }

        private static string CreateDisplayName(string localPath, string remotePath, NodeDto remoteRoot)
        {
            string localName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(localName))
            {
                return localName;
            }

            if (!string.IsNullOrWhiteSpace(remoteRoot.Name))
            {
                return remoteRoot.Name;
            }

            return remotePath;
        }

        private DesktopSyncApplicationHost RequireHost()
        {
            return _host ?? throw new InvalidOperationException("Sign in before running sync commands.");
        }

        private async Task UpdateSyncCoreStateAfterSyncPairDeletionAsync(
            DesktopSyncApplicationHost host,
            CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(_host, host))
            {
                return;
            }

            IReadOnlyList<SyncPairSettings> syncPairs = await host.App
                .ListSyncPairsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (syncPairs.Count == 0 && ReferenceEquals(_host, host))
            {
                _syncCoreState = SyncCoreStateStopped;
            }
        }

        private DesktopSyncApplicationHost? DetachHost()
        {
            DesktopSyncApplicationHost? host = _host;
            _host = null;
            _syncCoreState = SyncCoreStateSignedOut;
            _activitySubscription?.Dispose();
            _activitySubscription = null;
            _sessionRevocationSubscription?.Dispose();
            _sessionRevocationSubscription = null;
            _statusSubscription?.Dispose();
            _statusSubscription = null;
            _transferProgressSubscription?.Dispose();
            _transferProgressSubscription = null;
            _runProgressSubscription?.Dispose();
            _runProgressSubscription = null;
            return host;
        }

        private async Task ReplaceHostAsync(DesktopSyncApplicationHost host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesktopSyncApplicationHost? previous = _host;
            _host = host;
            _syncCoreState = SyncCoreStateStopped;
            _activitySubscription?.Dispose();
            _sessionRevocationSubscription?.Dispose();
            _statusSubscription?.Dispose();
            _transferProgressSubscription?.Dispose();
            _runProgressSubscription?.Dispose();
            _statusSubscription = host.StatusPublisher.Subscribe(new DesktopShellObserver<SyncAppStatus>(OnStatusChanged));
            _activitySubscription = host.ActivityPublisher.Subscribe(new DesktopShellObserver<AppSyncActivity>(OnActivityReported));
            _sessionRevocationSubscription = host.SessionRevocationPublisher.Subscribe(new DesktopShellObserver<SessionRevocationEvent>(OnSessionRevoked));
            _transferProgressSubscription = host.TransferProgressPublisher.Subscribe(new DesktopShellObserver<AppTransferProgress>(OnTransferProgressChanged));
            _runProgressSubscription = host.RunProgressPublisher.Subscribe(new DesktopShellObserver<AppRunProgress>(OnRunProgressChanged));
            if (previous is not null)
            {
                await StopAndDisposeHostAsync(previous).ConfigureAwait(false);
            }
        }

        private static async Task StopAndDisposeHostAsync(DesktopSyncApplicationHost host)
        {
            try
            {
                await host.App.StopSyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Failed to stop previous desktop sync host: {0}", exception);
            }
            finally
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static DesktopSyncStatusSnapshot ToStatusSnapshot(SyncAppStatus status)
        {
            return new DesktopSyncStatusSnapshot(
                status.SyncPairs
                    .Select(static syncPair => new DesktopSyncPairStatusSnapshot(
                        syncPair.SyncPairId,
                        ToStatusText(syncPair),
                        syncPair.LastError,
                        syncPair.CurrentOperation,
                        syncPair.LastSuccessfulSyncAtUtc))
                    .ToList());
        }

        private static string ToStatusText(SyncPairStatus status)
        {
            return status.State switch
            {
                SyncPairRunState.Disabled => "Disabled",
                SyncPairRunState.Idle => "Idle",
                SyncPairRunState.Scanning => "Scanning",
                SyncPairRunState.Syncing => "Syncing",
                SyncPairRunState.Paused => "Paused",
                SyncPairRunState.Offline => "Offline",
                SyncPairRunState.Conflict => "Conflict",
                SyncPairRunState.Error => "Error",
                _ => status.State.ToString(),
            };
        }

        private void OnStatusChanged(SyncAppStatus status)
        {
            StatusChanged?.Invoke(this, ToStatusSnapshot(status));
        }

        private void OnActivityReported(AppSyncActivity activity)
        {
            ActivityReported?.Invoke(this, ToActivitySnapshot(activity));
        }

        private void OnSessionRevoked(SessionRevocationEvent sessionRevocation)
        {
            DesktopAuthDiagnosticsState.RecordSessionRevoked(sessionRevocation.OccurredAtUtc);
            SessionRevoked?.Invoke(this, new DesktopSessionRevocationSnapshot(sessionRevocation.OccurredAtUtc));
        }

        private void OnTransferProgressChanged(AppTransferProgress progress)
        {
            TransferProgressChanged?.Invoke(this, ToTransferProgressSnapshot(progress));
        }

        private void OnRunProgressChanged(AppRunProgress progress)
        {
            RunProgressChanged?.Invoke(this, ToRunProgressSnapshot(progress));
        }

        private static DesktopActivitySnapshot ToActivitySnapshot(AppSyncActivity activity)
        {
            return new DesktopActivitySnapshot(
                activity.Type.ToString(),
                activity.ItemPath ?? string.Empty,
                activity.Message,
                activity.OccurredAtUtc,
                activity.SyncPairId);
        }

        private static DesktopTransferProgressSnapshot ToTransferProgressSnapshot(AppTransferProgress progress)
        {
            return new DesktopTransferProgressSnapshot(
                progress.SyncPairId,
                progress.Direction,
                progress.RelativePath,
                progress.TransferredBytes,
                progress.TotalBytes,
                progress.IsCompleted,
                progress.OccurredAtUtc,
                progress.SpeedBytesPerSecond,
                progress.EstimatedTimeRemaining);
        }

        private static DesktopRunProgressSnapshot ToRunProgressSnapshot(AppRunProgress progress)
        {
            return new DesktopRunProgressSnapshot(
                progress.SyncPairId,
                progress.Stage,
                progress.FilesCompleted,
                progress.FilesTotal,
                progress.CurrentPath,
                progress.StartedAtUtc,
                progress.IsCompleted,
                progress.OccurredAtUtc,
                progress.BytesCompleted,
                progress.BytesTotal);
        }

        private async Task<AuthSession?> TryRestoreSessionAsync(
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            if (!await CanUseStoredSessionAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            DesktopSyncApplicationHost host = _factory.Create(serverUrl);
            try
            {
                if (await host.TokenStore.GetAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedNoStoredTokens");
                    await host.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                using CancellationTokenSource restoreCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                restoreCancellation.CancelAfter(_savedSessionRestoreTimeout);
                RestoredSession restoredSession = await RestoreSessionWithRetryAsync(
                        host,
                        serverUrl,
                        restoreCancellation.Token)
                    .ConfigureAwait(false);
                DesktopAuthDiagnosticsState.RecordSessionRestoreSucceeded(restoredSession.Attempts);
                await ReplaceHostAsync(host, cancellationToken).ConfigureAwait(false);
                StartSessionSyncInBackground(host, "session restore");
                return restoredSession.Session;
            }
            catch (Cotton.Sdk.CottonApiException exception) when (IsAuthSessionRejected(exception))
            {
                Trace.TraceWarning("Failed to restore desktop session: {0}", exception);
                DesktopAuthDiagnosticsState.RecordSessionRestoreRejected(attempts: 1, exception);
                await host.TokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (Cotton.Sdk.CottonApiException exception)
            {
                DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                    "failed",
                    attempts: 1,
                    exception);
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning(
                    "Timed out restoring desktop session for {0} after {1} seconds.",
                    serverUrl,
                    _savedSessionRestoreTimeout.TotalSeconds);
                DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                    "timedOut",
                    attempts: 1,
                    new TimeoutException("Saved session restore timed out."));
                await host.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException("Saved session could not be restored. Check connection to Cotton Cloud and retry.");
            }
            catch (HttpRequestException)
            {
                Trace.TraceWarning("Failed to restore desktop session because the server is unreachable: {0}", serverUrl);
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task<RestoredSession> RestoreSessionWithRetryAsync(
            DesktopSyncApplicationHost host,
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= SavedSessionRestoreMaxAttempts; attempt++)
            {
                try
                {
                    AuthSession session = await host.App.RestoreSessionAsync(cancellationToken)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return new RestoredSession(session, attempt);
                }
                catch (Exception exception) when (IsTransientSessionRestoreFailure(exception, cancellationToken))
                {
                    if (attempt == SavedSessionRestoreMaxAttempts)
                    {
                        Trace.TraceWarning(
                            "Failed to restore desktop session because the server is unreachable after {0} attempts: {1}",
                            SavedSessionRestoreMaxAttempts,
                            serverUrl);
                        DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                            "transientFailure",
                            attempt,
                            exception);
                        throw;
                    }

                    TimeSpan delay = TimeSpan.FromTicks(_savedSessionRestoreRetryBaseDelay.Ticks * attempt);
                    Trace.TraceWarning(
                        "Desktop session restore attempt {0} failed transiently for {1}. Retrying after {2} seconds.",
                        attempt,
                        serverUrl,
                        delay.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("Desktop session restore retry loop exited unexpectedly.");
        }

        private readonly record struct RestoredSession(AuthSession Session, int Attempts);

        private static bool IsTransientSessionRestoreFailure(Exception exception, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return exception is HttpRequestException
                or IOException
                or TimeoutException
                or TaskCanceledException;
        }

        private static bool IsAuthSessionRejected(Cotton.Sdk.CottonApiException exception)
        {
            return exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        }

        private void StartSessionSyncInBackground(DesktopSyncApplicationHost host, string source)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    _syncCoreState = SyncCoreStateStarting;
                    await host.App.StartSyncAsync(CancellationToken.None).ConfigureAwait(false);
                    if (ReferenceEquals(_host, host))
                    {
                        _syncCoreState = SyncCoreStateRunning;
                    }
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Failed to start desktop sync after {0}: {1}", source, exception);
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    _syncCoreState = SyncCoreStateStartFailed;
                    ActivityReported?.Invoke(
                        this,
                        new DesktopActivitySnapshot(
                            "Error",
                            string.Empty,
                            DesktopActionRequiredMessageResolver.FromException(exception),
                            DateTime.UtcNow));
                }
            });
        }

        private void StartInitialSyncInBackground(DesktopSyncApplicationHost host, Guid syncPairId, string localPath)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    await host.App.SyncNowAsync(syncPairId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "Failed to request initial sync for newly added Cotton Sync folder {0}: {1}",
                        syncPairId,
                        exception);
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    ActivityReported?.Invoke(
                        this,
                        new DesktopActivitySnapshot(
                            "Error",
                            localPath,
                            DesktopActionRequiredMessageResolver.FromException(exception),
                            DateTime.UtcNow));
                }
            });
        }

        private async Task EnsureReleaseSecureTokenStorageAsync(CancellationToken cancellationToken)
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage = await _tokenStorageVerifier(cancellationToken)
                .ConfigureAwait(false);
            if (tokenStorage.IsReleaseSecure)
            {
                return;
            }

            throw new InvalidOperationException(CreateTokenStorageUnavailableMessage(tokenStorage));
        }

        private async Task<bool> CanUseStoredSessionAsync(CancellationToken cancellationToken)
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage;
            using CancellationTokenSource verificationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationCancellation.CancelAfter(_tokenStorageVerificationTimeout);
            try
            {
                tokenStorage = await _tokenStorageVerifier(verificationCancellation.Token)
                    .WaitAsync(verificationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning(
                    "Skipping desktop session restore because token storage verification timed out after {0} seconds.",
                    _tokenStorageVerificationTimeout.TotalSeconds);
                DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedTokenStorageVerificationTimeout");
                return false;
            }

            if (tokenStorage.IsReleaseSecure)
            {
                return true;
            }

            Trace.TraceWarning(
                "Skipping desktop session restore because token storage is not release secure: {0}",
                tokenStorage.Details);
            DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedTokenStorageUnavailable");
            return false;
        }

        private static string CreateTokenStorageUnavailableMessage(DesktopTokenStorageCapabilitySnapshot tokenStorage)
        {
            return "Secure token storage is unavailable: "
                + tokenStorage.Details
                + ". Configure Windows DPAPI or Linux Secret Service before signing in.";
        }

    }
}
