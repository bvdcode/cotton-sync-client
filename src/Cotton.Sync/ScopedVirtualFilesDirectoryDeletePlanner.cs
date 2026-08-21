// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal static class ScopedVirtualFilesDirectoryDeletePlanner
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static ScopedVirtualFilesDirectoryDeletePlan? Build(
            SyncPair syncPair,
            SyncRunOptions options,
            ScopedVirtualFilesDirectoryDeleteContext context)
        {
            if (options.Scope.IsFull
                || syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return null;
            }

            IReadOnlySet<string> deletedKeys = BuildExactScopedPathKeys(options.Scope.LocalDeletedPaths);
            List<string> candidateKeys = context.DirectoryStateByPath.Keys
                .Where(key =>
                    deletedKeys.Contains(key)
                    && !context.LocalDirectoriesByPath.ContainsKey(key)
                    && context.RemoteDirectoriesByPath.ContainsKey(key))
                .ToList();
            string[] rootKeys = candidateKeys
                .Where(candidate => candidateKeys.All(other =>
                    PathComparer.Equals(candidate, other)
                    || !IsSameOrDescendantPathKey(candidate, other)))
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (rootKeys.Length == 0)
            {
                return null;
            }

            HashSet<string> directoryKeys = new(PathComparer);
            HashSet<string> fileKeys = new(PathComparer);
            List<string> rootPaths = [];
            foreach (string rootKey in rootKeys)
            {
                ScopedVirtualFilesDirectoryDeleteRoot? root = TryCreateConfirmedScopedDirectoryDeleteRoot(
                    context,
                    rootKey);
                if (root is null)
                {
                    return null;
                }

                rootPaths.Add(root.RelativePath);
                directoryKeys.UnionWith(root.DirectoryKeys);
                fileKeys.UnionWith(root.FileKeys);
            }

            string[] orderedFileKeys = fileKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
            return new ScopedVirtualFilesDirectoryDeletePlan(
                rootPaths,
                directoryKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                orderedFileKeys,
                orderedFileKeys.Select(key => context.FileStateByPath[key].RelativePath).ToArray());
        }

        private static ScopedVirtualFilesDirectoryDeleteRoot? TryCreateConfirmedScopedDirectoryDeleteRoot(
            ScopedVirtualFilesDirectoryDeleteContext context,
            string rootKey)
        {
            bool hasLocalSubtree = context.LocalDirectoriesByPath.Keys.Any(
                    key => IsSameOrDescendantPathKey(key, rootKey))
                || context.LocalFilesByPath.Keys.Any(key => IsSameOrDescendantPathKey(key, rootKey));
            if (hasLocalSubtree)
            {
                return null;
            }

            HashSet<string> expectedDirectoryKeys = context.DirectoryStateByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> expectedFileKeys = context.FileStateByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> actualDirectoryKeys = context.RemoteDirectoriesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> actualFileKeys = context.RemoteFilesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            if (!actualDirectoryKeys.SetEquals(expectedDirectoryKeys)
                || !actualFileKeys.SetEquals(expectedFileKeys)
                || !HaveMatchingScopedDeleteDirectoryIds(context, expectedDirectoryKeys)
                || !HaveMatchingScopedDeleteFileBaselines(context, expectedFileKeys))
            {
                return null;
            }

            return new ScopedVirtualFilesDirectoryDeleteRoot(
                context.DirectoryStateByPath[rootKey].RelativePath,
                expectedDirectoryKeys,
                expectedFileKeys);
        }

        private static bool HaveMatchingScopedDeleteDirectoryIds(
            ScopedVirtualFilesDirectoryDeleteContext context,
            IEnumerable<string> directoryKeys)
        {
            foreach (string key in directoryKeys)
            {
                if (context.DirectoryStateByPath[key].RemoteNodeId != context.RemoteDirectoriesByPath[key].Node.Id)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HaveMatchingScopedDeleteFileBaselines(
            ScopedVirtualFilesDirectoryDeleteContext context,
            IEnumerable<string> fileKeys)
        {
            foreach (string key in fileKeys)
            {
                SyncStateEntry state = context.FileStateByPath[key];
                RemoteFileSnapshot remote = context.RemoteFilesByPath[key];
                if (state.RemoteFileId != remote.File.Id || !RemoteMatchesBaseline(remote.File, state))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
