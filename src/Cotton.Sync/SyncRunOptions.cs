// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    /// <summary>
    /// Defines options for one synchronization pass.
    /// </summary>
    public class SyncRunOptions
    {
        private const int DefaultMaximumDeletesPerRun = 100;
        private const int DefaultMaximumStoredResultActivities = 1_000;
        private const int DefaultInitialVirtualFilesPopulationQueueCapacity = 2_048;
        private const int DefaultInitialVirtualFilesStateBatchSize = 512;
        private const int DefaultInitialVirtualFilesPlaceholderConcurrency = 4;
        private const int DefaultInitialVirtualFilesPlaceholderBatchSize = 64;

        /// <summary>
        /// Gets or sets the filesystem surface reconciled by this pass.
        /// </summary>
        public SyncRunScope Scope { get; set; } = SyncRunScope.Full;

        /// <summary>
        /// Gets or sets a value indicating whether remote file deletes bypass trash.
        /// </summary>
        public bool DeleteRemotePermanently { get; set; }

        /// <summary>
        /// Gets or sets how old a local file's last write timestamp must be before upload starts.
        /// Background clients can use this to coalesce write storms and avoid uploading transient intermediate content.
        /// </summary>
        public TimeSpan MinimumLocalUploadAge { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets or sets the maximum number of local files that can be removed in one sync pass.
        /// </summary>
        public int MaximumLocalDeletesPerRun { get; set; } = DefaultMaximumDeletesPerRun;

        /// <summary>
        /// Gets or sets the maximum number of remote files that can be removed in one sync pass.
        /// </summary>
        public int MaximumRemoteDeletesPerRun { get; set; } = DefaultMaximumDeletesPerRun;

        /// <summary>
        /// Gets or sets the maximum number of activities retained in the returned result.
        /// Live activity progress is still reported for every activity.
        /// </summary>
        public int MaximumStoredResultActivities { get; set; } = DefaultMaximumStoredResultActivities;

        /// <summary>
        /// Gets or sets the bounded queue size used while initial Windows virtual-files discovery streams placeholders to disk.
        /// </summary>
        public int InitialVirtualFilesPopulationQueueCapacity { get; set; } = DefaultInitialVirtualFilesPopulationQueueCapacity;

        /// <summary>
        /// Gets or sets how many initial Windows virtual-file placeholder state entries are flushed per durable write batch.
        /// </summary>
        public int InitialVirtualFilesStateBatchSize { get; set; } = DefaultInitialVirtualFilesStateBatchSize;

        /// <summary>
        /// Gets or sets how many Windows virtual-file placeholders can be created concurrently during initial population.
        /// </summary>
        public int InitialVirtualFilesPlaceholderConcurrency { get; set; } = DefaultInitialVirtualFilesPlaceholderConcurrency;

        /// <summary>
        /// Gets or sets how many Windows virtual-file placeholders are submitted to a batch-capable writer at once during initial population.
        /// </summary>
        public int InitialVirtualFilesPlaceholderBatchSize { get; set; } = DefaultInitialVirtualFilesPlaceholderBatchSize;

        /// <summary>
        /// Gets or sets the optional live activity reporter used by UI and CLI clients.
        /// </summary>
        public IProgress<SyncActivity>? ActivityProgress { get; set; }

        /// <summary>
        /// Gets or sets the optional live transfer-progress reporter used by UI clients.
        /// </summary>
        public IProgress<SyncTransferProgress>? TransferProgress { get; set; }

        /// <summary>
        /// Gets or sets the optional aggregate sync-pass progress reporter used by UI clients.
        /// </summary>
        public IProgress<SyncRunProgress>? RunProgress { get; set; }

        /// <summary>
        /// Gets or sets the optional cooperative scheduler yield used after large reconciliation batches.
        /// UI and tray clients can leave this unset; the sync engine will yield to the .NET scheduler.
        /// Tests can inject a deterministic callback to verify batch boundaries without timing sleeps.
        /// </summary>
        public Func<CancellationToken, ValueTask>? CooperativeYieldAsync { get; set; }
    }
}
