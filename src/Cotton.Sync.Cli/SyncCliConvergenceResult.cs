// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Cli
{
    internal record SyncCliConvergenceResult(
        SyncCliPassResult Pass,
        bool Converged,
        int Passes);
}
