// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private void MarkHydratingDirectoriesPending(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> entries,
            IReadOnlyDictionary<string, WindowsVirtualFileDiskState?> initialDiskStates)
        {
            HashSet<string> pendingDirectoryKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (SyncStateEntry entry in entries)
            {
                if (!IsTrackedVirtualFile(entry))
                {
                    continue;
                }
                WindowsVirtualFileDiskState? diskState = initialDiskStates[SyncPath.ToKey(entry.RelativePath)];
                if (diskState is null || !IsHydrationComplete(diskState.Attributes, entry.PlaceholderHydrationState))
                {
                    AddAncestorDirectoryKeys(entry.RelativePath, pendingDirectoryKeys);
                }
            }
            foreach (SyncStateEntry entry in entries)
            {
                if (entry.Kind == SyncEntryKind.Directory
                    && pendingDirectoryKeys.Contains(SyncPath.ToKey(entry.RelativePath)))
                {
                    _cloudFiles.SetInSyncState(syncPair, entry.RelativePath, inSync: false);
                }
            }
        }

        private void MarkAncestorDirectoriesPending(
            SyncPairSettings syncPair,
            string relativePath,
            ISet<string> pendingDirectoryKeys)
        {
            string directoryPath = SyncPath.Normalize(relativePath);
            int separator = directoryPath.LastIndexOf('/');
            while (separator > 0)
            {
                directoryPath = directoryPath[..separator];
                if (pendingDirectoryKeys.Add(SyncPath.ToKey(directoryPath)))
                {
                    _cloudFiles.SetInSyncState(syncPair, directoryPath, inSync: false);
                }
                separator = directoryPath.LastIndexOf('/');
            }
        }
    }
}
