// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal class DesktopLifecycleCleanupContext
    {
        public required IAsyncDisposable? OwnedController { get; init; }

        public required SyncApplicationService? Application { get; init; }

        public required bool SyncCoreStopped { get; init; }

        public required bool PairDeleted { get; init; }

        public required IWindowsCloudFilesAdapter CloudFiles { get; init; }

        public required SyncPairSettings SyncPair { get; init; }

        public required TextWriter Output { get; init; }

        public required string StopFailureLabel { get; init; }
    }
}
