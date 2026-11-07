// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class ObservedEmptyDirectoryRenameCoordinator(
        IRemoteDirectorySynchronizer? remoteDirectories,
        ISyncStateStore stateStore)
    {
        public async Task CoalesceAsync(SyncRunContext context)
        {
            if (remoteDirectories is null || context.Options.Scope.LocalRenames.Count == 0)
            {
                return;
            }

            IReadOnlyDictionary<string, string> targets = LocalRenameTargetResolver.Resolve(
                context.DirectoryStateByPath, context.Options.Scope.LocalRenames);
            foreach ((string sourceKey, string targetKey) in targets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!context.DirectoryStateByPath.TryGetValue(sourceKey, out SyncStateEntry? source)
                    || context.LocalDirectoriesByPath.ContainsKey(sourceKey)
                    || !context.LocalDirectoriesByPath.TryGetValue(targetKey, out LocalDirectorySnapshot? target)
                    || context.DirectoryStateByPath.ContainsKey(targetKey)
                    || context.RemoteDirectoriesByPath.ContainsKey(targetKey)
                    || !context.RemoteDirectoriesByPath.TryGetValue(sourceKey, out RemoteDirectorySnapshot? remote)
                    || source.RemoteNodeId != remote.Node.Id
                    || !string.Equals(GetParentPath(source.RelativePath), GetParentPath(target.RelativePath),
                        StringComparison.OrdinalIgnoreCase)
                    || HasDescendants(context.RemoteDirectoriesByPath.Keys, sourceKey)
                    || HasDescendants(context.RemoteFilesByPath.Keys, sourceKey)
                    || await HasStoredDescendantsAsync(context, source.RelativePath).ConfigureAwait(false)
                    || await HasStoredEntriesAsync(context, target.RelativePath).ConfigureAwait(false))
                {
                    continue;
                }

                NodeDto renamed = await remoteDirectories.RenameDirectoryAsync(
                    remote.Node.Id, GetFileName(target.RelativePath), context.CancellationToken).ConfigureAwait(false);
                if (renamed.Id != remote.Node.Id || renamed.ParentId != remote.Node.ParentId)
                {
                    throw new InvalidDataException("Remote folder rename changed its identity or parent.");
                }

                SyncStateEntry movedState = SyncBaselineFactory.BuildDirectoryBaseline(
                    context.SyncPair, target.RelativePath, renamed);
                await stateStore.UpsertAsync(movedState, context.CancellationToken).ConfigureAwait(false);
                await stateStore.DeleteAsync(context.SyncPair.SyncPairId, source.RelativePath,
                    context.CancellationToken).ConfigureAwait(false);
                context.DirectoryStateByPath.Remove(sourceKey);
                context.DirectoryStateByPath[targetKey] = movedState;
                context.RemoteDirectoriesByPath.Remove(sourceKey);
                context.RemoteDirectoriesByPath[targetKey] = new RemoteDirectorySnapshot
                {
                    RelativePath = target.RelativePath,
                    Node = renamed,
                };
                SyncActivityReporter.Record(context.Result, context.Options, SyncActivityKind.Converged,
                    target.RelativePath, "Renamed remote folder while preserving its identity.");
            }
        }

        private async Task<bool> HasStoredDescendantsAsync(SyncRunContext context, string path)
        {
            await foreach (SyncStateEntry entry in stateStore.LoadEntriesByPathPrefixAsync(
                               context.SyncPair.SyncPairId, path, context.CancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(SyncPath.ToKey(entry.RelativePath), SyncPath.ToKey(path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> HasStoredEntriesAsync(SyncRunContext context, string path)
        {
            await foreach (SyncStateEntry _ in stateStore.LoadEntriesByPathPrefixAsync(
                               context.SyncPair.SyncPairId, path, context.CancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }

        private static bool HasDescendants(IEnumerable<string> keys, string pathKey)
        {
            string prefix = pathKey + "/";
            return keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
