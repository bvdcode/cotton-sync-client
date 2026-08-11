// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Startup
{
    internal class DesktopLiveSyncSmokeSession(
        DesktopAppPaths firstPaths,
        DesktopAppPaths secondPaths,
        DesktopShellController firstController,
        DesktopShellController secondController)
    {
        public DesktopAppPaths FirstPaths { get; } = firstPaths;

        public DesktopAppPaths SecondPaths { get; } = secondPaths;

        public DesktopShellController FirstController { get; } = firstController;

        public DesktopShellController SecondController { get; } = secondController;

        public bool FirstSignedIn { get; set; }

        public bool SecondSignedIn { get; set; }

        public SyncPairSettings? FirstPair { get; set; }

        public SyncPairSettings? SecondPair { get; set; }
    }
}
