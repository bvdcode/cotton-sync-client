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
    internal partial class DesktopShellController
    {
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

            UpsertKnownSyncPairSettings(syncPair);
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
                RemoveKnownSyncPairSettings(syncPairId);
                var stateStore = new SqliteSyncStateStore(_paths.SyncStateDatabasePath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPairId.ToString(), cancellationToken).ConfigureAwait(false);
                return;
            }

            await host.App.DeleteSyncPairAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            RemoveKnownSyncPairSettings(syncPairId);
            await UpdateSyncCoreStateAfterSyncPairDeletionAsync(host, cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveSyncPairSettingsAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is null)
            {
                await _syncPairStore.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                UpsertKnownSyncPairSettings(syncPair);
                return;
            }

            SyncPairSaveResult result = await host.App
                .SaveSyncPairAsync(syncPair, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSaved)
            {
                throw new SyncPairValidationException(result.Validation.Errors);
            }

            UpsertKnownSyncPairSettings(syncPair);
        }

        private async Task<IReadOnlyList<DesktopSyncPairSnapshot>> BuildSyncPairSnapshotsAsync(
            IReadOnlyList<SyncPairSettings> settings,
            CancellationToken cancellationToken)
        {
            if (settings.Count == 0)
            {
                ReplaceKnownSyncPairSettings(settings);
                return [];
            }

            ReplaceKnownSyncPairSettings(settings);
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
            string? localRootError = GetLocalRootUnavailableError(settings);
            string statusText = ResolveSyncPairStatusText(settings, status, localRootError);
            return new DesktopSyncPairSnapshot(
                settings.Id,
                settings.DisplayName,
                settings.LocalRootPath,
                settings.RemoteDisplayPath,
                statusText,
                settings.RemoteRootNodeId,
                lastSyncedAtUtc,
                cursor?.LastCursor,
                localRootError ?? status?.LastError,
                settings.Mode,
                cursor?.HasCompletedFullReconcile ?? false);
        }

        private static string ResolveSyncPairStatusText(
            SyncPairSettings settings,
            SyncPairStatus? status,
            string? localRootError)
        {
            if (localRootError is not null)
            {
                return "Error";
            }

            if (status is not null)
            {
                return ToStatusText(status);
            }

            return settings.IsEnabled ? "Idle" : "Disabled";
        }

        private void ReplaceKnownSyncPairSettings(IReadOnlyList<SyncPairSettings> settings)
        {
            lock (_syncPairSettingsGate)
            {
                _knownSyncPairSettings = settings.ToDictionary(
                    static syncPair => syncPair.Id,
                    static syncPair => (syncPair.IsEnabled, syncPair.LocalRootPath));
            }
        }

        private void UpsertKnownSyncPairSettings(SyncPairSettings settings)
        {
            lock (_syncPairSettingsGate)
            {
                _knownSyncPairSettings[settings.Id] = (settings.IsEnabled, settings.LocalRootPath);
            }
        }

        private void RemoveKnownSyncPairSettings(Guid syncPairId)
        {
            lock (_syncPairSettingsGate)
            {
                _knownSyncPairSettings.Remove(syncPairId);
            }
        }

        private IReadOnlyDictionary<Guid, (bool IsEnabled, string LocalRootPath)> GetKnownSyncPairSettingsSnapshot()
        {
            lock (_syncPairSettingsGate)
            {
                return new Dictionary<Guid, (bool IsEnabled, string LocalRootPath)>(_knownSyncPairSettings);
            }
        }

        private static string? GetLocalRootUnavailableError(SyncPairSettings settings)
        {
            return GetLocalRootUnavailableError(settings.IsEnabled, settings.LocalRootPath);
        }

        private static string? GetLocalRootUnavailableError(bool isEnabled, string localRootPath)
        {
            if (!isEnabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(localRootPath))
            {
                return LocalRootUnavailableError;
            }

            return Directory.Exists(localRootPath)
                ? null
                : LocalRootUnavailableError;
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
    }
}
