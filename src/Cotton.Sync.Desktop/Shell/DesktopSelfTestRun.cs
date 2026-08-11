// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Shell
{
    internal class DesktopSelfTestRun
    {
        public List<DesktopSelfTestItemSnapshot> Items { get; } = [];

        public AppPreferences? Preferences { get; set; }

        public IReadOnlyList<SyncPairSettings> SyncPairs { get; set; } = [];
    }
}
