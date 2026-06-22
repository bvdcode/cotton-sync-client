// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal class PendingLocalSyncRequest
    {
        public const int MaxScopedChangedPaths = 1024;
        public const int MaxWindowsVirtualFilesScopedChangedPaths = 65_536;

        public PendingLocalSyncRequest(CancellationTokenSource cancellation, string changedPath)
        {
            Cancellation = cancellation;
            ChangedPath = changedPath;
            ChangedPaths.Add(changedPath);
        }

        public CancellationTokenSource Cancellation { get; }

        public string ChangedPath { get; private set; }

        public HashSet<string> ChangedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ChangeVersion { get; private set; }

        public bool RequiresFullSync { get; private set; }

        public Task? Runner { get; set; }

        public void RecordChange(
            string changedPath,
            bool requiresFullSync,
            int maxScopedChangedPaths = MaxScopedChangedPaths)
        {
            ChangedPath = changedPath;
            if (requiresFullSync || RequiresFullSync)
            {
                RequiresFullSync = true;
                ChangedPaths.Clear();
                ChangeVersion++;
                return;
            }

            if (!ChangedPaths.Contains(changedPath) && ChangedPaths.Count >= maxScopedChangedPaths)
            {
                RequiresFullSync = true;
                ChangedPaths.Clear();
                ChangeVersion++;
                return;
            }

            ChangedPaths.Add(changedPath);
            ChangeVersion++;
        }
    }
}
