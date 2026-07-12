// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.LocalChanges
{
    internal class PendingLocalSyncRequest
    {
        public const int MaxScopedChangedPaths = 1024;
        public const int MaxWindowsVirtualFilesScopedChangedPaths = 4_096;

        public PendingLocalSyncRequest(
            CancellationTokenSource cancellation,
            string changedPath,
            DateTimeOffset createdAt)
        {
            Cancellation = cancellation;
            ChangedPath = changedPath;
            CreatedAt = createdAt;
            ChangedPaths.Add(changedPath);
        }

        public CancellationTokenSource Cancellation { get; }

        public DateTimeOffset CreatedAt { get; }

        public string ChangedPath { get; private set; }

        public HashSet<string> ChangedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DeletedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ChangeVersion { get; private set; }

        public SyncRunCause Causes { get; private set; }

        public bool FlushRequested { get; private set; }

        public bool RequiresFullSync { get; private set; }

        public Task? Runner { get; set; }

        public void RecordChange(
            string changedPath,
            SyncRunCause fullSyncCause,
            int maxScopedChangedPaths = MaxScopedChangedPaths,
            bool preserveScopeOnOverflow = false,
            bool isDeleted = false)
        {
            ChangedPath = changedPath;
            if (fullSyncCause != SyncRunCause.None || RequiresFullSync)
            {
                RequiresFullSync = true;
                Causes |= fullSyncCause;
                ChangedPaths.Clear();
                DeletedPaths.Clear();
                ChangeVersion++;
                return;
            }

            Causes |= SyncRunCause.LocalChange;
            if (!ChangedPaths.Contains(changedPath) && ChangedPaths.Count >= maxScopedChangedPaths)
            {
                Causes |= SyncRunCause.LocalChangeOverflow;
                if (preserveScopeOnOverflow)
                {
                    FlushRequested = true;
                }

                RequiresFullSync = true;
                ChangedPaths.Clear();
                DeletedPaths.Clear();
                ChangeVersion++;
                return;
            }

            ChangedPaths.Add(changedPath);
            if (isDeleted)
            {
                DeletedPaths.Add(changedPath);
            }

            ChangeVersion++;
        }
    }
}
