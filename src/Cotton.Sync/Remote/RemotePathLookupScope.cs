// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal class RemotePathLookupScope
    {
        public Dictionary<string, RemotePathLookupScope> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IncludesDescendants { get; private set; }

        public static RemotePathLookupScope Create(IEnumerable<string> relativePaths)
        {
            RemotePathLookupScope root = new();
            foreach (string path in SyncPathOperations.BuildScopedRelativePaths(relativePaths))
            {
                RemotePathLookupScope current = root;
                foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (current.IncludesDescendants)
                    {
                        break;
                    }

                    if (!current.Children.TryGetValue(segment, out RemotePathLookupScope? child))
                    {
                        child = new RemotePathLookupScope();
                        current.Children.Add(segment, child);
                    }

                    current = child;
                }

                current.IncludesDescendants = true;
                current.Children.Clear();
            }

            return root;
        }
    }
}
