// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Observes large provider-side placeholder population so app layers can suppress provider-generated watcher churn.
    /// </summary>
    public interface IRemoteFilePlaceholderPopulationObserver
    {
        /// <summary>
        /// Begins a provider-side placeholder population scope.
        /// </summary>
        IDisposable BeginPopulation(string syncPairId, string localRootPath);
    }
}
