// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Supervision;

namespace Cotton.Sync.App.SyncApplication
{
    internal class SyncPauseController
    {
        private readonly IAppPreferencesStore _preferences;
        private readonly ISyncSupervisor _supervisor;

        public SyncPauseController(IAppPreferencesStore preferences, ISyncSupervisor supervisor)
        {
            _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        public async Task<bool> LoadAsync(CancellationToken cancellationToken)
        {
            await _preferences.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferences.GetAsync(cancellationToken).ConfigureAwait(false);
            return preferences.IsSyncPaused;
        }

        public async Task PauseAllAsync(CancellationToken cancellationToken)
        {
            await SaveAsync(isPaused: true, cancellationToken).ConfigureAwait(false);
            await _supervisor.PauseAllAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task ResumeAllAsync(CancellationToken cancellationToken)
        {
            await SaveAsync(isPaused: false, cancellationToken).ConfigureAwait(false);
            await _supervisor.ResumeAllAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            return SaveAsync(isPaused: false, cancellationToken);
        }

        private async Task SaveAsync(bool isPaused, CancellationToken cancellationToken)
        {
            await _preferences.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferences.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.IsSyncPaused = isPaused;
            await _preferences.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
    }
}
