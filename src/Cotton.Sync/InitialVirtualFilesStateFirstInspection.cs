// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesStateFirstInspection
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public int EntriesSeen { get; private set; }

        public int DirectoryEntries { get; private set; }

        public int FileEntries { get; private set; }

        public int OnlineOnlyFileEntries { get; private set; }

        public int MaterializedFileEntries { get; private set; }

        public HashSet<string> DirectoryStateKeys { get; } = new(PathComparer);

        public Dictionary<string, InitialVirtualFilesPlaceholderBaseline> FileBaselineByPath { get; } =
            new(PathComparer);

        public List<string> StateRelativePaths { get; } = [];

        public string? Add(SyncStateEntry entry)
        {
            EntriesSeen++;
            switch (entry.Kind)
            {
                case SyncEntryKind.Directory:
                    return AddDirectory(entry);
                case SyncEntryKind.File:
                    return AddFile(entry);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(entry),
                        entry.Kind,
                        "Unknown sync state entry kind.");
            }
        }

        public string? FindLocalIncompatibility(LocalTreeLookupSnapshot localStateLookups)
        {
            foreach (string directoryKey in DirectoryStateKeys)
            {
                if (!localStateLookups.DirectoriesByPath.ContainsKey(directoryKey))
                {
                    return "tracked directory placeholder is missing locally";
                }
            }

            foreach ((string fileKey, InitialVirtualFilesPlaceholderBaseline baseline) in FileBaselineByPath)
            {
                if (!localStateLookups.FilesByPath.TryGetValue(fileKey, out LocalFileSnapshot? local)
                    || !InitialVirtualFilesPlaceholderPolicy.IsResumeCompatible(local, baseline))
                {
                    return "tracked file placeholder is missing or incompatible with its persisted availability state";
                }
            }

            return null;
        }

        private string? AddDirectory(SyncStateEntry entry)
        {
            if (entry.RemoteNodeId is null)
            {
                return "directory state is missing a remote folder id";
            }

            DirectoryEntries++;
            DirectoryStateKeys.Add(SyncPath.ToKey(entry.RelativePath));
            StateRelativePaths.Add(entry.RelativePath);
            return null;
        }

        private string? AddFile(SyncStateEntry entry)
        {
            FileEntries++;
            if (!InitialVirtualFilesPlaceholderPolicy.HasRemoteBaseline(entry))
            {
                return "file state is missing a remote baseline";
            }

            if (InitialVirtualFilesPlaceholderPolicy.IsOnlineOnly(entry))
            {
                OnlineOnlyFileEntries++;
            }
            else
            {
                MaterializedFileEntries++;
            }

            FileBaselineByPath[SyncPath.ToKey(entry.RelativePath)] = InitialVirtualFilesPlaceholderBaseline.FromState(entry);
            StateRelativePaths.Add(entry.RelativePath);
            return null;
        }

    }
}
