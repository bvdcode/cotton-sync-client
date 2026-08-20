// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    internal static class RemoteTreePath
    {
        public static string Combine(string parentPath, string name)
        {
            string combined = string.IsNullOrWhiteSpace(parentPath)
                ? name
                : parentPath + "/" + name;
            return SyncPath.Normalize(combined);
        }
    }
}
