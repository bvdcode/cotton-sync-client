// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Startup
{
    internal class AlwaysKeepPopulationWorkContext
    {
        public SyncPairSettings SyncPair { get; init; } = null!;

        public ISyncPairWork AvailabilityWork { get; init; } = null!;

        public IRemoteFilePlaceholderPopulationObserver PopulationObserver { get; init; } = null!;

        public Func<string, CancellationToken, Task> CreateDirectoryAsync { get; init; } = null!;

        public Func<string, CancellationToken, Task> CreateFileAsync { get; init; } = null!;

        public TaskCompletionSource<bool> EarlyPopulationReady { get; init; } = null!;

        public TaskCompletionSource<bool> ContinuePopulation { get; init; } = null!;

        public Func<bool> EvaluateLateDescendantAvailability { get; init; } = null!;

        public bool LateDescendantsInheritedAvailability { get; set; }
    }
}
