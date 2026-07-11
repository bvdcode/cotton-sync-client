// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    internal record RemoteChangeScopedSyncPlan(
        SyncRunRequest? Request,
        bool HasUnresolvedChanges);
}
