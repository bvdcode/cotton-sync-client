// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal static class LocalRenameTargetResolver
    {
        public static IReadOnlyDictionary<string, string> Resolve(
            IEnumerable<KeyValuePair<string, SyncStateEntry>> sources,
            IReadOnlyList<LocalPathRename> renames)
        {
            Dictionary<string, string> originsByCurrentPath = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> targetsByOrigin = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, SyncStateEntry> source in sources)
            {
                originsByCurrentPath[source.Key] = source.Key;
            }

            foreach (LocalPathRename rename in renames)
            {
                string sourceKey = SyncPath.ToKey(rename.SourcePath);
                string targetKey = SyncPath.ToKey(rename.TargetPath);
                if (!originsByCurrentPath.Remove(sourceKey, out string? origin))
                {
                    continue;
                }

                if (originsByCurrentPath.Remove(targetKey, out string? displacedOrigin))
                {
                    targetsByOrigin.Remove(displacedOrigin);
                }

                originsByCurrentPath[targetKey] = origin;
                targetsByOrigin[origin] = targetKey;
            }

            return targetsByOrigin;
        }
    }
}
