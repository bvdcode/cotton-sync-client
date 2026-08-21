// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal static class SyncPathOperations
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static string CombineRelativePath(string parentPath, string childPath)
        {
            string normalizedChild = childPath.Replace(Path.DirectorySeparatorChar, '/');
            return SyncPath.Normalize(parentPath + "/" + normalizedChild);
        }

        public static string ReplacePathPrefix(string path, string sourcePrefix, string targetPrefix)
        {
            string normalizedPath = SyncPath.Normalize(path);
            string normalizedSource = SyncPath.Normalize(sourcePrefix);
            string normalizedTarget = SyncPath.Normalize(targetPrefix);
            if (PathComparer.Equals(normalizedPath, normalizedSource))
            {
                return normalizedTarget;
            }

            return normalizedTarget + normalizedPath[normalizedSource.Length..];
        }

        public static string ResolveLocalPath(string localRootPath, string relativePath)
        {
            return Path.Combine(
                Path.GetFullPath(localRootPath),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static int GetPathDepth(string relativePath)
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? 0
                : relativePath.Count(static character => character == '/') + 1;
        }

        public static bool IsSameOrDescendantPathKey(string pathKey, string directoryKey)
        {
            return PathComparer.Equals(pathKey, directoryKey)
                || pathKey.StartsWith(directoryKey.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetParentPath(string relativePath)
        {
            string normalized = SyncPath.Normalize(relativePath);
            int lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex < 0 ? string.Empty : normalized[..lastSlashIndex];
        }

        public static string GetFileName(string relativePath)
        {
            string normalized = SyncPath.Normalize(relativePath);
            int lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex < 0 ? normalized : normalized[(lastSlashIndex + 1)..];
        }

        public static Dictionary<string, T> ToDictionary<T>(
            IEnumerable<T> entries,
            Func<T, string> pathSelector)
        {
            Dictionary<string, T> result = new(PathComparer);
            foreach (T entry in entries)
            {
                string relativePath = SyncPath.Normalize(pathSelector(entry));
                if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                {
                    continue;
                }

                string key = SyncPath.ToKey(relativePath);
                if (result.TryGetValue(key, out T? existing))
                {
                    throw new SyncPathCollisionException(pathSelector(existing), relativePath);
                }

                NormalizeSnapshotPath(entry, relativePath);
                result[key] = entry;
            }

            return result;
        }

        public static void ThrowIfPathKindCollisions<TLeft, TRight>(
            IReadOnlyDictionary<string, TLeft> left,
            IReadOnlyDictionary<string, TRight> right,
            Func<TLeft, string> leftPathSelector,
            Func<TRight, string> rightPathSelector)
        {
            foreach (KeyValuePair<string, TLeft> item in left)
            {
                if (right.TryGetValue(item.Key, out TRight? colliding))
                {
                    throw new SyncPathCollisionException(leftPathSelector(item.Value), rightPathSelector(colliding));
                }
            }
        }

        public static IReadOnlyList<string> BuildPathKeys(params IEnumerable<string>[] keySets)
        {
            List<string> keys = BuildUniquePathKeyList(keySets);
            keys.Sort(PathComparer.Compare);
            return keys;
        }

        public static IReadOnlyList<string> BuildDirectoryPathKeys(params IEnumerable<string>[] keySets)
        {
            List<string> keys = BuildUniquePathKeyList(keySets);
            keys.Sort(static (left, right) =>
            {
                int depthComparison = GetPathDepth(left).CompareTo(GetPathDepth(right));
                return depthComparison != 0
                    ? depthComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });
            return keys;
        }

        public static IReadOnlyList<string> BuildScopedRelativePaths(IEnumerable<string> relativePaths)
        {
            HashSet<string> yielded = new(PathComparer);
            List<string> paths = [];
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                if (yielded.Add(SyncPath.ToKey(normalizedPath)))
                {
                    paths.Add(normalizedPath);
                }
            }

            return paths;
        }

        public static bool ShouldIncludeScopedDirectoryDescendants(SyncPair syncPair)
        {
            return syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles;
        }

        public static IEnumerable<string> BuildScopedPathKeys(IEnumerable<string> relativePaths)
        {
            HashSet<string> yielded = new(PathComparer);
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string current = string.Empty;
                for (int index = 0; index < segments.Length; index++)
                {
                    current = string.IsNullOrEmpty(current) ? segments[index] : current + "/" + segments[index];
                    string key = SyncPath.ToKey(current);
                    if (yielded.Add(key))
                    {
                        yield return key;
                    }
                }
            }
        }

        public static IEnumerable<string> EnumerateDirectoryDeleteKeys(IReadOnlyList<string> pathKeys)
        {
            for (int index = pathKeys.Count - 1; index >= 0;)
            {
                int depth = GetPathDepth(pathKeys[index]);
                int groupStart = index;
                while (groupStart > 0 && GetPathDepth(pathKeys[groupStart - 1]) == depth)
                {
                    groupStart--;
                }

                for (int groupIndex = groupStart; groupIndex <= index; groupIndex++)
                {
                    yield return pathKeys[groupIndex];
                }

                index = groupStart - 1;
            }
        }

        public static IReadOnlySet<string> BuildExactScopedPathKeys(IEnumerable<string> relativePaths)
        {
            HashSet<string> keys = new(PathComparer);
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                keys.Add(SyncPath.ToKey(normalizedPath));
            }

            return keys;
        }

        public static IReadOnlySet<string> AddScopedPathKeys(
            IReadOnlySet<string> existingKeys,
            IEnumerable<string> additionalKeys)
        {
            HashSet<string> keys = new(existingKeys, PathComparer);
            keys.UnionWith(additionalKeys);
            return keys;
        }

        private static void NormalizeSnapshotPath<T>(T entry, string relativePath)
        {
            switch (entry)
            {
                case LocalDirectorySnapshot directory:
                    directory.RelativePath = relativePath;
                    break;
                case LocalFileSnapshot file:
                    file.RelativePath = relativePath;
                    break;
                case RemoteDirectorySnapshot directory:
                    directory.RelativePath = relativePath;
                    break;
                case RemoteFileSnapshot file:
                    file.RelativePath = relativePath;
                    break;
            }
        }

        public static List<string> BuildUniquePathKeyList(params IEnumerable<string>[] keySets)
        {
            if (TryBuildSingleSourcePathKeyList(keySets, out List<string> singleSourceKeys))
            {
                return singleSourceKeys;
            }

            int initialCapacity = EstimateUniquePathKeyCapacity(keySets);
            HashSet<string> seen = new(initialCapacity, PathComparer);
            List<string> keys = new(initialCapacity);
            foreach (IEnumerable<string> keySet in keySets)
            {
                foreach (string key in keySet)
                {
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys;
        }

        private static bool TryBuildSingleSourcePathKeyList(
            IEnumerable<string>[] keySets,
            out List<string> keys)
        {
            IEnumerable<string>? singleSource = null;
            int singleSourceCount = 0;
            foreach (IEnumerable<string> keySet in keySets)
            {
                if (!keySet.TryGetNonEnumeratedCount(out int count))
                {
                    keys = [];
                    return false;
                }

                if (count == 0)
                {
                    continue;
                }

                if (singleSource is not null)
                {
                    keys = [];
                    return false;
                }

                singleSource = keySet;
                singleSourceCount = count;
            }

            keys = singleSource is null ? [] : new List<string>(singleSourceCount);
            if (singleSource is not null)
            {
                keys.AddRange(singleSource);
            }

            return true;
        }

        private static int EstimateUniquePathKeyCapacity(IEnumerable<string>[] keySets)
        {
            int capacity = 0;
            foreach (IEnumerable<string> keySet in keySets)
            {
                if (keySet.TryGetNonEnumeratedCount(out int count) && count > capacity)
                {
                    capacity = count;
                }
            }

            return capacity;
        }
    }
}
