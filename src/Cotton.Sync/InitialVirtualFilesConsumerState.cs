// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesConsumerState(
        int stateBatchSize,
        int placeholderBatchSize,
        int placeholderConcurrency,
        bool trackDirectoryFinalization,
        StringComparer pathComparer)
    {
        public List<SyncStateEntry> PendingFileStates { get; } = new(stateBatchSize);

        public List<SyncStateEntry> PendingDirectoryStates { get; } = new(stateBatchSize);

        public List<RemoteFileSnapshot> PendingFileBatch { get; } = new(placeholderBatchSize);

        public List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> PendingFileTasks { get; } =
            new(placeholderConcurrency);

        public Dictionary<string, RemoteDirectoryMaterializationRequest>? DirectoryFinalizationRequests { get; } =
            trackDirectoryFinalization
                ? new Dictionary<string, RemoteDirectoryMaterializationRequest>(pathComparer)
                : null;

        public HashSet<string> StreamedRemoteFileKeys { get; } = new(pathComparer);

        public Dictionary<string, string> StreamedRemotePathByKey { get; } = new(pathComparer);
    }
}
