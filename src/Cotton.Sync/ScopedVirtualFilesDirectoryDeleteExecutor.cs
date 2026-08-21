// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class ScopedVirtualFilesDirectoryDeleteExecutor(
        IRemoteDirectorySynchronizer? remoteDirectories,
        ISyncStateStore stateStore)
    {
        public async Task DeleteConfirmedScopedVirtualFilesDirectoryRenameSourceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            ScopedVirtualFilesDirectoryRenamePlan plan,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (plan.SourceFileKeys.Any(key => remoteFilesByPath.ContainsKey(key) || fileStateByPath.ContainsKey(key)))
            {
                return;
            }

            if (remoteDirectories is null)
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    plan.SourceRootPath,
                    "Remote folder cleanup is not available after the confirmed local folder move.",
                    requiresUserAction: true);
                return;
            }

            foreach (string key in plan.SourceDirectoryKeys)
            {
                if (!directoryStateByPath.TryGetValue(key, out SyncStateEntry? state)
                    || !remoteDirectoriesByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote)
                    || state.RemoteNodeId != remote.Node.Id)
                {
                    return;
                }
            }

            string[] remoteDeletePlanItems = plan.SourceDirectoryKeys
                .Select(key => RemoteDeletePlanFingerprint.CreateDirectoryItem(
                    key,
                    remoteDirectoriesByPath[key].Node.Id))
                .ToArray();
            SyncDeleteGuard deleteGuard = new(options, plannedLocalDeletes: 0, remoteDeletePlanItems);
            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    plan.SourceRootPath,
                    details,
                    requiresUserAction: true);
                return;
            }

            foreach (string key in plan.SourceDirectoryKeys
                         .OrderByDescending(GetPathDepth)
                         .ThenBy(static key => key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncStateEntry state = directoryStateByPath[key];
                RemoteDirectorySnapshot remote = remoteDirectoriesByPath[key];
                await remoteDirectories
                    .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                    .ConfigureAwait(false);
                await stateStore
                    .DeleteAsync(syncPair.SyncPairId, state.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                directoryStateByPath.Remove(key);
                remoteDirectoriesByPath.Remove(key);
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.DeletedRemote,
                    state.RelativePath,
                    "Deleted source folder after confirmed local subtree move.");
            }
        }

        public async Task DeleteConfirmedScopedVirtualFilesDirectorySubtreesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            ScopedVirtualFilesDirectoryDeletePlan plan,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            CancellationToken cancellationToken)
        {
            if (await HasRemainingScopedVirtualFilesAsync(
                    syncPair.SyncPairId,
                    plan.FilePaths,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            IRemoteDirectorySynchronizer? synchronizer = remoteDirectories;
            if (synchronizer is null)
            {
                ReportScopedDirectoryDeleteSkipped(
                    result,
                    options,
                    plan.RootPaths,
                    "Remote folder cleanup is not available after the confirmed local subtree delete.");
                return;
            }

            if (!AreScopedRemoteDirectoriesCurrent(
                    plan.DirectoryKeys,
                    remoteDirectoriesByPath,
                    directoryStateByPath))
            {
                return;
            }

            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                ReportScopedDirectoryDeleteSkipped(result, options, plan.RootPaths, details);
                return;
            }

            await DeleteScopedRemoteDirectoriesAsync(
                syncPair,
                options,
                result,
                synchronizer,
                plan.DirectoryKeys,
                remoteDirectoriesByPath,
                directoryStateByPath,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> HasRemainingScopedVirtualFilesAsync(
            string syncPairId,
            IEnumerable<string> relativePaths,
            CancellationToken cancellationToken)
        {
            foreach (string relativePath in relativePaths)
            {
                SyncStateEntry? remaining = await stateStore
                    .GetAsync(syncPairId, relativePath, cancellationToken)
                    .ConfigureAwait(false);
                if (remaining is not null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AreScopedRemoteDirectoriesCurrent(
            IEnumerable<string> directoryKeys,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (string key in directoryKeys)
            {
                if (!directoryStateByPath.TryGetValue(key, out SyncStateEntry? state)
                    || !remoteDirectoriesByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote)
                    || state.RemoteNodeId != remote.Node.Id)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ReportScopedDirectoryDeleteSkipped(
            SyncRunResult result,
            SyncRunOptions options,
            IEnumerable<string> rootPaths,
            string? details)
        {
            foreach (string rootPath in rootPaths)
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    rootPath,
                    details,
                    requiresUserAction: true);
            }
        }

        private async Task DeleteScopedRemoteDirectoriesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IRemoteDirectorySynchronizer remoteDirectories,
            IEnumerable<string> directoryKeys,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            CancellationToken cancellationToken)
        {
            foreach (string key in directoryKeys
                         .OrderByDescending(GetPathDepth)
                         .ThenBy(static key => key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncStateEntry state = directoryStateByPath[key];
                RemoteDirectorySnapshot remote = remoteDirectoriesByPath[key];
                await remoteDirectories
                    .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                    .ConfigureAwait(false);
                await stateStore
                    .DeleteAsync(syncPair.SyncPairId, state.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                directoryStateByPath.Remove(key);
                remoteDirectoriesByPath.Remove(key);
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.DeletedRemote,
                    state.RelativePath,
                    "Deleted folder after confirmed local subtree delete.");
            }
        }
    }
}
