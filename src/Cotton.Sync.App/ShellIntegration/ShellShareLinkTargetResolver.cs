// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.App.ShellIntegration
{
    public class ShellShareLinkTargetResolver : IShellShareLinkTargetResolver
    {
        private readonly ISyncPairSettingsStore _syncPairs;
        private readonly ISyncStateStore _syncState;

        public ShellShareLinkTargetResolver(
            ISyncPairSettingsStore syncPairs,
            ISyncStateStore syncState)
        {
            _syncPairs = syncPairs ?? throw new ArgumentNullException(nameof(syncPairs));
            _syncState = syncState ?? throw new ArgumentNullException(nameof(syncState));
        }

        public async Task<ShellShareLinkTarget> ResolveAsync(
            string localPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            ShellShareLinkLocalPath selectedPath = ShellShareLinkLocalPath.Normalize(localPath);
            await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairs.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            SyncPairSettings? syncPair = syncPairs
                .Where(pair => ShellShareLinkLocalPath.TryNormalize(
                        pair.LocalRootPath,
                        out ShellShareLinkLocalPath root)
                    && root.ContainsOrEquals(selectedPath))
                .OrderByDescending(pair => ShellShareLinkLocalPath.Normalize(pair.LocalRootPath).Length)
                .FirstOrDefault();
            if (syncPair is null)
            {
                return new ShellShareLinkTarget(ShellShareLinkTargetStatus.OutsideSyncRoot);
            }

            if (!syncPair.IsEnabled)
            {
                return new ShellShareLinkTarget(
                    ShellShareLinkTargetStatus.SyncPairDisabled,
                    syncPair.Id);
            }

            ShellShareLinkLocalPath syncRootPath = ShellShareLinkLocalPath.Normalize(syncPair.LocalRootPath);
            string relativePath = syncRootPath.GetRelativePath(selectedPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return new ShellShareLinkTarget(
                    ShellShareLinkTargetStatus.Resolved,
                    syncPair.Id,
                    relativePath,
                    ShellShareLinkTargetKind.Directory,
                    RemoteNodeId: syncPair.RemoteRootNodeId);
            }

            if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
            {
                return new ShellShareLinkTarget(
                    ShellShareLinkTargetStatus.IgnoredPath,
                    syncPair.Id,
                    relativePath);
            }

            await _syncState.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncStateEntry? state = await _syncState
                .GetAsync(syncPair.Id.ToString("D"), relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                return new ShellShareLinkTarget(
                    ShellShareLinkTargetStatus.MissingBaseline,
                    syncPair.Id,
                    relativePath);
            }

            return state.Kind switch
            {
                SyncEntryKind.Directory => state.RemoteNodeId.HasValue
                    ? new ShellShareLinkTarget(
                        ShellShareLinkTargetStatus.Resolved,
                        syncPair.Id,
                        state.RelativePath,
                        ShellShareLinkTargetKind.Directory,
                        RemoteNodeId: state.RemoteNodeId)
                    : new ShellShareLinkTarget(
                        ShellShareLinkTargetStatus.MissingRemoteIdentity,
                        syncPair.Id,
                        state.RelativePath,
                        ShellShareLinkTargetKind.Directory),
                SyncEntryKind.File => state.RemoteFileId.HasValue
                    ? new ShellShareLinkTarget(
                        ShellShareLinkTargetStatus.Resolved,
                        syncPair.Id,
                        state.RelativePath,
                        ShellShareLinkTargetKind.File,
                        RemoteFileId: state.RemoteFileId)
                    : new ShellShareLinkTarget(
                        ShellShareLinkTargetStatus.MissingRemoteIdentity,
                        syncPair.Id,
                        state.RelativePath,
                        ShellShareLinkTargetKind.File),
                _ => throw new ArgumentOutOfRangeException(nameof(state), state.Kind, "Unknown sync state entry kind."),
            };
        }

    }
}
