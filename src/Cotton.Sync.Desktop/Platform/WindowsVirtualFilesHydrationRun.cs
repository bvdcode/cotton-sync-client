// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsVirtualFilesHydrationRun(
        SyncRunRequest request,
        IReadOnlyDictionary<string, WindowsVirtualFileDiskState?> initialDiskStates)
    {
        public SyncRunRequest Request { get; } = request;

        public IReadOnlyDictionary<string, WindowsVirtualFileDiskState?> InitialDiskStates { get; } =
            initialDiskStates;

        public int TotalFiles { get; } = initialDiskStates.Count;

        public int CompletedFiles { get; set; }

        public int HydratedFiles { get; set; }

        public int AlreadyHydratedFiles { get; set; }

        public DateTime StartedAt { get; } = DateTime.UtcNow;
    }
}
