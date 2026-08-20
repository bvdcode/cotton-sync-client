// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.App.SyncApplication
{
    /// <summary>
    /// Provides high-level sync-client commands over validated application state.
    /// </summary>
    public class SyncApplicationService : ISyncApplicationService
    {
        private readonly SemaphoreSlim _syncCoreGate = new(1, 1);
        private readonly IAppCodeBrowserAuthFlow _appCodeBrowserAuthFlow;
        private readonly IAuthFlow _authFlow;
        private readonly IPlatformCommandService _platformCommands;
        private readonly IAppPreferencesStore _preferences;
        private readonly ISyncPairPrerequisiteValidator _prerequisites;
        private readonly SyncCoreComponentHost _syncCoreComponents;
        private readonly SyncPauseController _syncPause;
        private readonly ISyncStateStore? _syncStateStore;
        private readonly ISyncPairDeletionHandler _syncPairDeletionHandler;
        private readonly ISyncSupervisor _supervisor;
        private readonly ISyncPairSettingsStore _syncPairs;
        private readonly SyncPairSettingsValidator _validator;
        private readonly ILogger<SyncApplicationService> _logger;
        private bool _isSyncCoreStarted;
        private bool _startSyncCoreWhenSyncPairsExist;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncApplicationService" /> class.
        /// </summary>
        public SyncApplicationService(
            ISyncPairSettingsStore syncPairs,
            ISyncPairPrerequisiteValidator prerequisites,
            IAppPreferencesStore preferences,
            IAuthFlow authFlow,
            IAppCodeBrowserAuthFlow appCodeBrowserAuthFlow,
            ISyncSupervisor supervisor,
            IPlatformCommandService platformCommands,
            ILocalChangeSyncCoordinator? localChanges = null,
            IRemoteChangeSyncCoordinator? remoteChanges = null,
            IPeriodicSyncCoordinator? periodicSync = null,
            IEnumerable<ISyncCoreLifecycleComponent>? syncCoreLifecycleComponents = null,
            ISyncStateStore? syncStateStore = null,
            SyncPairSettingsValidator? validator = null,
            ISyncPairDeletionHandler? syncPairDeletionHandler = null,
            ILogger<SyncApplicationService>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(syncPairs);
            ArgumentNullException.ThrowIfNull(prerequisites);
            ArgumentNullException.ThrowIfNull(preferences);
            ArgumentNullException.ThrowIfNull(authFlow);
            ArgumentNullException.ThrowIfNull(appCodeBrowserAuthFlow);
            ArgumentNullException.ThrowIfNull(supervisor);
            ArgumentNullException.ThrowIfNull(platformCommands);

            _syncPairs = syncPairs;
            _prerequisites = prerequisites;
            _preferences = preferences;
            _authFlow = authFlow;
            _appCodeBrowserAuthFlow = appCodeBrowserAuthFlow;
            _supervisor = supervisor;
            _platformCommands = platformCommands;
            _syncStateStore = syncStateStore;
            _validator = validator ?? new SyncPairSettingsValidator();
            _syncPairDeletionHandler = syncPairDeletionHandler ?? NullSyncPairDeletionHandler.Instance;
            _logger = logger ?? NullLogger<SyncApplicationService>.Instance;
            _syncCoreComponents = new SyncCoreComponentHost(
                supervisor,
                localChanges ?? NullLocalChangeSyncCoordinator.Instance,
                remoteChanges ?? NullRemoteChangeSyncCoordinator.Instance,
                periodicSync ?? NullPeriodicSyncCoordinator.Instance,
                (syncCoreLifecycleComponents ?? []).ToList(),
                _logger);
            _syncPause = new SyncPauseController(preferences, supervisor);
        }

        /// <inheritdoc />
        public Task<AuthSession> SignInAsync(
            PasswordSignInRequest request,
            CancellationToken cancellationToken = default) =>
            _authFlow.SignInAsync(request, cancellationToken);

        /// <inheritdoc />
        public Task<AuthSession> SignInWithBrowserAsync(
            AppCodeBrowserSignInRequest request,
            CancellationToken cancellationToken = default) =>
            _appCodeBrowserAuthFlow.SignInAsync(request, cancellationToken);

        /// <inheritdoc />
        public Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            _authFlow.RestoreSessionAsync(cancellationToken);

        /// <inheritdoc />
        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            await StopSyncCoreAsync(cancellationToken).ConfigureAwait(false);
            await _authFlow.SignOutAsync(cancellationToken).ConfigureAwait(false);
            await _syncPause.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AppPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
        {
            await _preferences.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await _preferences.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            await _preferences.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _preferences.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SyncPairSettings>> ListSyncPairsAsync(CancellationToken cancellationToken = default)
        {
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await _syncPairs.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<SyncPairSettings?> GetSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await _syncPairs.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<SyncPairSaveResult> SaveSyncPairAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            List<SyncPairSettings> current = (await _syncPairs.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int existingIndex = current.FindIndex(item => item.Id == syncPair.Id);
            SyncPairSettings? existingSyncPair = existingIndex >= 0 ? current[existingIndex] : null;
            if (existingIndex >= 0)
            {
                current[existingIndex] = syncPair;
            }
            else
            {
                current.Add(syncPair);
            }

            SyncPairValidationResult validation = _validator.Validate(current);
            if (!validation.IsValid)
            {
                return SyncPairSaveResult.Rejected(validation);
            }

            SyncPairValidationError? scopeChangeError = SyncPairSavePolicy.ValidateScopeChange(
                existingSyncPair,
                syncPair);
            if (scopeChangeError is not null)
            {
                return SyncPairSaveResult.Rejected(new SyncPairValidationResult([scopeChangeError]));
            }

            if (SyncPairSavePolicy.RequiresPrerequisiteValidation(existingSyncPair, syncPair))
            {
                IReadOnlyList<SyncPairValidationError> prerequisiteErrors = await _prerequisites
                    .ValidateAsync(syncPair, cancellationToken)
                    .ConfigureAwait(false);
                if (prerequisiteErrors.Count > 0)
                {
                    return SyncPairSaveResult.Rejected(new SyncPairValidationResult(prerequisiteErrors));
                }
            }

            await _syncPairs.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
            await RefreshSyncCoreAfterSyncPairSaveAsync(cancellationToken).ConfigureAwait(false);
            return SyncPairSaveResult.Saved(validation);
        }

        /// <inheritdoc />
        public async Task DeleteSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            SyncPairDeletionContext context = new();
            await _syncCoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteSyncPairUnlockedAsync(syncPairId, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RestoreSyncCoreAfterInterruptedDeleteAsync(context).ConfigureAwait(false);
                _syncCoreGate.Release();
            }
        }

        private async Task DeleteSyncPairUnlockedAsync(
            Guid syncPairId,
            SyncPairDeletionContext context,
            CancellationToken cancellationToken)
        {
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncPairSettings? syncPair = await _syncPairs.GetAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            if (_isSyncCoreStarted)
            {
                await StopSyncCoreUnlockedAsync(cancellationToken, force: false).ConfigureAwait(false);
                context.SyncCoreWasRunning = true;
            }

            if (syncPair is not null)
            {
                await _syncPairDeletionHandler.BeforeDeleteAsync(syncPair, cancellationToken).ConfigureAwait(false);
            }

            await _syncPairs.DeleteAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            context.SyncPairSettingsDeleted = true;
            await DeleteSyncPairStateAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            await ConfigureSyncCoreAfterDeleteAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private async Task DeleteSyncPairStateAsync(Guid syncPairId, CancellationToken cancellationToken)
        {
            if (_syncStateStore is null)
            {
                return;
            }

            await _syncStateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _syncStateStore.DeletePairAsync(syncPairId.ToString(), cancellationToken).ConfigureAwait(false);
        }

        private async Task ConfigureSyncCoreAfterDeleteAsync(
            SyncPairDeletionContext context,
            CancellationToken cancellationToken)
        {
            bool hasConfiguredSyncPairs = await HasConfiguredSyncPairsAsync(cancellationToken).ConfigureAwait(false);
            if (context.SyncCoreWasRunning && hasConfiguredSyncPairs)
            {
                context.RestartAttempted = true;
                await StartSyncCoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!hasConfiguredSyncPairs)
            {
                context.KeepSyncCoreStoppedUntilPairAdded = context.SyncCoreWasRunning;
                _startSyncCoreWhenSyncPairsExist = true;
            }
        }

        private async Task RestoreSyncCoreAfterInterruptedDeleteAsync(SyncPairDeletionContext context)
        {
            if (!context.SyncCoreWasRunning
                || context.RestartAttempted
                || _isSyncCoreStarted
                || context.KeepSyncCoreStoppedUntilPairAdded)
            {
                return;
            }

            try
            {
                if (await ShouldRestartSyncCoreAfterDeleteFailureAsync(context).ConfigureAwait(false))
                {
                    await StartSyncCoreUnlockedAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to restart sync core after sync pair deletion was interrupted.");
            }
        }

        private async Task<bool> ShouldRestartSyncCoreAfterDeleteFailureAsync(SyncPairDeletionContext context)
        {
            if (!context.SyncPairSettingsDeleted)
            {
                return true;
            }

            try
            {
                bool hasConfiguredSyncPairs = await HasConfiguredSyncPairsAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (!hasConfiguredSyncPairs)
                {
                    _startSyncCoreWhenSyncPairsExist = true;
                }

                return hasConfiguredSyncPairs;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to inspect sync pairs after deletion was interrupted; restarting sync core.");
                return true;
            }
        }

        /// <inheritdoc />
        public Task StartSyncAsync(CancellationToken cancellationToken = default)
        {
            return StartSyncCoreAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task SyncAllAsync(CancellationToken cancellationToken = default)
        {
            return _supervisor.SyncAllAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            return _supervisor.SyncNowAsync(syncPairId, cancellationToken);
        }

        /// <inheritdoc />
        public Task SyncNowAsync(
            Guid syncPairId,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            return _supervisor.SyncNowAsync(syncPairId, request, cancellationToken);
        }

        /// <inheritdoc />
        public Task PauseAllAsync(CancellationToken cancellationToken = default)
        {
            return _syncPause.PauseAllAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            return _supervisor.PauseAsync(syncPairId, cancellationToken);
        }

        /// <inheritdoc />
        public Task ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            return _syncPause.ResumeAllAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            return _supervisor.ResumeAsync(syncPairId, cancellationToken);
        }

        /// <inheritdoc />
        public Task StopSyncAsync(CancellationToken cancellationToken = default)
        {
            return StopSyncCoreAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
        {
            return _platformCommands.OpenFolderAsync(localPath, cancellationToken);
        }

        /// <inheritdoc />
        public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
        {
            return _platformCommands.OpenWebAsync(url, cancellationToken);
        }

        private async Task StartSyncCoreAsync(CancellationToken cancellationToken)
        {
            await _syncCoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StartSyncCoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _syncCoreGate.Release();
            }
        }

        private async Task StopSyncCoreAsync(CancellationToken cancellationToken)
        {
            await _syncCoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _startSyncCoreWhenSyncPairsExist = false;
                await StopSyncCoreUnlockedAsync(cancellationToken, force: true).ConfigureAwait(false);
            }
            finally
            {
                _syncCoreGate.Release();
            }
        }

        private async Task RefreshSyncCoreAfterSyncPairSaveAsync(CancellationToken cancellationToken)
        {
            await _syncCoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isSyncCoreStarted)
                {
                    await StopSyncCoreUnlockedAsync(cancellationToken, force: false).ConfigureAwait(false);
                    await StartSyncCoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!_startSyncCoreWhenSyncPairsExist)
                {
                    return;
                }

                if (await HasConfiguredSyncPairsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await StartSyncCoreUnlockedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _syncCoreGate.Release();
            }
        }

        private async Task StartSyncCoreUnlockedAsync(CancellationToken cancellationToken)
        {
            if (_isSyncCoreStarted)
            {
                await StopSyncCoreUnlockedAsync(cancellationToken, force: false).ConfigureAwait(false);
            }

            try
            {
                bool isSyncGloballyPaused = await _syncPause.LoadAsync(cancellationToken).ConfigureAwait(false);
                await _syncCoreComponents.StartAsync(isSyncGloballyPaused, cancellationToken).ConfigureAwait(false);
                _isSyncCoreStarted = true;
                _startSyncCoreWhenSyncPairsExist = false;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to start sync background components.");
                _isSyncCoreStarted = false;
                if (!cancellationToken.IsCancellationRequested)
                {
                    _startSyncCoreWhenSyncPairsExist = true;
                }

                throw;
            }
        }

        private async Task StopSyncCoreUnlockedAsync(CancellationToken cancellationToken, bool force)
        {
            if (!_isSyncCoreStarted && !force)
            {
                return;
            }

            await _syncCoreComponents.StopAsync(cancellationToken).ConfigureAwait(false);

            _isSyncCoreStarted = false;
        }

        private async Task<bool> HasConfiguredSyncPairsAsync(CancellationToken cancellationToken)
        {
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairs.ListAsync(cancellationToken).ConfigureAwait(false);
            return syncPairs.Count > 0;
        }

    }
}
