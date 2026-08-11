// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsVirtualFilesAvailabilityRun(
        SyncRunRequest request,
        HashSet<string> manualDehydrationPathKeys)
    {
        public SyncRunRequest Request { get; } = request;

        public HashSet<string> ManualDehydrationPathKeys { get; } = manualDehydrationPathKeys;

        public int CompletedManualDehydrations { get; set; }

        public int TotalManualDehydrations { get; set; } = manualDehydrationPathKeys.Count;

        public bool ManualDehydrationProgressStarted { get; set; }

        public DateTime ManualDehydrationStartedAt { get; } = DateTime.UtcNow;

        public List<string> RemainingPaths { get; } = [];

        public HashSet<string> HandledAvailabilityPathKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HandledRootAvailability { get; set; }

        public bool RequiresFullPass { get; set; }

        public bool CurrentDehydrationStarted { get; set; }
    }
}
