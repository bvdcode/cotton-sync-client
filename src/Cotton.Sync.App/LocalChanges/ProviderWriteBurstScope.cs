// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal class ProviderWriteBurstScope
    {
        public ProviderWriteBurstScope(string rootPath)
        {
            RootPath = rootPath;
            ActiveCount = 1;
            ExpiresAt = DateTimeOffset.MaxValue;
        }

        public string RootPath { get; set; }

        public int ActiveCount { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public HashSet<string> RegisteredPathKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
