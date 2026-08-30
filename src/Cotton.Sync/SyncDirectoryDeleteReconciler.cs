// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class SyncDirectoryDeleteReconciler(
        IRemoteDirectorySynchronizer? remoteDirectories,
        ISyncStateStore stateStore,
        ILocalFileSyncWriter localWriter)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task ReconcileAsync(DirectoryDeleteContext context)
        {
            foreach (string key in EnumerateDirectoryDeleteKeys(context.PathKeys))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!TryGetDeleteState(context, key, out SyncStateEntry? state))
                {
                    continue;
                }

                context.LocalByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                context.RemoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                string relativePath = SyncDirectoryReconciler.ResolveRelativePath(key, local, remote, state);
                await ReconcileDeleteAsync(context, key, relativePath, local, remote).ConfigureAwait(false);
            }
        }

        public async Task ReconcileEmptyLocalDirectoriesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            foreach (string key in EnumerateDirectoryDeleteKeys(pathKeys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stateByPath.ContainsKey(key)
                    || !localByPath.TryGetValue(key, out LocalDirectorySnapshot? local)
                    || remoteByPath.ContainsKey(key)
                    || !Directory.Exists(local.FullPath))
                {
                    continue;
                }

                string relativePath = local.RelativePath;
                if (DirectoryHasFileSystemEntries(local.FullPath))
                {
                    continue;
                }

                await DeleteLocalDirectoryAsync(
                        syncPair,
                        options,
                        result,
                        deleteGuard,
                        relativePath,
                        local,
                        DirectoryContentIndex.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static bool TryGetDeleteState(
            DirectoryDeleteContext context,
            string key,
            out SyncStateEntry? state)
        {
            if (context.PlannedScopedDeleteKeys?.Contains(key, PathComparer) == true)
            {
                state = null;
                return false;
            }

            if (context.ScopedDeleteKeys is not null && !context.ScopedDeleteKeys.Contains(key))
            {
                state = null;
                return false;
            }

            return context.StateByPath.TryGetValue(key, out state);
        }

        private async Task ReconcileDeleteAsync(
            DirectoryDeleteContext context,
            string key,
            string relativePath,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote)
        {
            if (local is null && remote is null)
            {
                await stateStore.DeleteAsync(
                        context.SyncPair.SyncPairId,
                        relativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (local is null && remote is not null)
            {
                if (IsUnconfirmedScopedVirtualFilesDirectoryDelete(context, key))
                {
                    SyncActivityReporter.Record(
                        context.Result,
                        context.Options,
                        SyncActivityKind.Skipped,
                        relativePath,
                        "Remote folder delete could not be confirmed because its tracked subtree was not fully resolved.",
                        requiresUserAction: true);
                    return;
                }

                await DeleteRemoteDirectoryAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.DeleteGuard,
                        relativePath,
                        remote,
                        context.RemoteContentIndex,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (remote is null && local is not null)
            {
                await ReconcileRemoteDeletedDirectoryAsync(context, key, relativePath, local).ConfigureAwait(false);
            }
        }

        private static bool IsUnconfirmedScopedVirtualFilesDirectoryDelete(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            return context.SyncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && !context.Options.Scope.IsFull
                && context.ScopedLocalDeletedKeys.Contains(directoryKey)
                && context.PlannedScopedDeleteKeys?.Contains(directoryKey, PathComparer) != true;
        }

        private async Task ReconcileRemoteDeletedDirectoryAsync(
            DirectoryDeleteContext context,
            string key,
            string relativePath,
            LocalDirectorySnapshot local)
        {
            bool isNotEmpty = context.LocalContentIndex.HasChildren(relativePath)
                || DirectoryHasFileSystemEntries(local.FullPath);
            if (isNotEmpty)
            {
                if (CanDeferConfirmedRemoteDeletedDirectory(context, key))
                {
                    return;
                }

                SyncActivityReporter.Record(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Local folder delete skipped because the folder is not empty.");
                return;
            }

            await DeleteLocalDirectoryAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.DeleteGuard,
                    relativePath,
                    local,
                    context.LocalContentIndex,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static bool CanDeferConfirmedRemoteDeletedDirectory(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            if (!context.LocalByPath.TryGetValue(directoryKey, out LocalDirectorySnapshot? rootDirectory))
            {
                return false;
            }

            return !HasBlockingTrackedDirectoryDescendant(context, directoryKey)
                && !HasBlockingTrackedFileDescendant(context, directoryKey)
                && !HasUnknownFileSystemDirectory(rootDirectory, context.LocalByPath)
                && !HasUnknownFileSystemFile(rootDirectory, context.LocalFilesByPath);
        }

        private static bool HasBlockingTrackedDirectoryDescendant(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            foreach (string childDirectoryKey in context.LocalByPath.Keys
                         .Where(key => !PathComparer.Equals(key, directoryKey)
                             && IsSameOrDescendantPathKey(key, directoryKey)))
            {
                if (!context.StateByPath.ContainsKey(childDirectoryKey)
                    || context.RemoteByPath.ContainsKey(childDirectoryKey))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasBlockingTrackedFileDescendant(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            foreach (string childFileKey in context.LocalFilesByPath.Keys
                         .Where(key => IsSameOrDescendantPathKey(key, directoryKey)))
            {
                if (!context.FileStateByPath.ContainsKey(childFileKey)
                    || context.RemoteFilesByPath.ContainsKey(childFileKey))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUnknownFileSystemDirectory(
            LocalDirectorySnapshot rootDirectory,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath)
        {
            foreach (string childDirectoryPath in Directory.EnumerateDirectories(
                         rootDirectory.FullPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relativePath = CombineRelativePath(
                    rootDirectory.RelativePath,
                    Path.GetRelativePath(rootDirectory.FullPath, childDirectoryPath));
                if (!SyncPathIgnoreRules.ShouldIgnore(relativePath)
                    && !localDirectoriesByPath.ContainsKey(SyncPath.ToKey(relativePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUnknownFileSystemFile(
            LocalDirectorySnapshot rootDirectory,
            IReadOnlyDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            foreach (string childFilePath in Directory.EnumerateFiles(
                         rootDirectory.FullPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relativePath = CombineRelativePath(
                    rootDirectory.RelativePath,
                    Path.GetRelativePath(rootDirectory.FullPath, childFilePath));
                if (!SyncPathIgnoreRules.ShouldIgnore(relativePath)
                    && !localFilesByPath.ContainsKey(SyncPath.ToKey(relativePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task DeleteRemoteDirectoryAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            RemoteDirectorySnapshot remote,
            DirectoryContentIndex remoteDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            if (remoteDirectories is null)
            {
                SyncActivityReporter.Record(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Remote folder delete is not available.");
                return;
            }

            if (remoteDirectoryContentIndex.HasChildren(relativePath))
            {
                SyncActivityReporter.Record(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Remote folder delete skipped because the folder is not empty.");
                return;
            }

            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                SyncActivityReporter.Record(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    details,
                    requiresUserAction: true);
                return;
            }

            await remoteDirectories
                .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                .ConfigureAwait(false);
            await stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.Record(
                result,
                options,
                SyncActivityKind.DeletedRemote,
                relativePath,
                "Deleted remote folder.");
        }

        private async Task DeleteLocalDirectoryAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            LocalDirectorySnapshot localDirectory,
            DirectoryContentIndex localDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            if (localDirectoryContentIndex.HasChildren(relativePath)
                || DirectoryHasFileSystemEntries(localDirectory.FullPath))
            {
                SyncActivityReporter.Record(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Local folder delete skipped because the folder is not empty.");
                return;
            }

            if (!deleteGuard.CanDeleteLocal(out string? details))
            {
                SyncActivityReporter.Record(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    details,
                    requiresUserAction: true);
                return;
            }

            await localWriter.DeleteDirectoryAsync(syncPair.LocalRootPath, relativePath, cancellationToken)
                .ConfigureAwait(false);
            await stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.Record(
                result,
                options,
                SyncActivityKind.DeletedLocal,
                relativePath,
                "Deleted local folder.");
        }

        private static bool DirectoryHasFileSystemEntries(string fullPath)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(fullPath).Any();
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
    }
}
