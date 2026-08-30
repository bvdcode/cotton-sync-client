// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using SuppressionEntry = Cotton.Sync.App.LocalChanges.LocalChangeSuppressionEntry;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Suppresses short-lived filesystem watcher echoes produced by provider-side virtual file work.
    /// </summary>
    public partial class LocalChangeSuppression : ILocalChangeSuppression
    {
        private static readonly TimeSpan DefaultEntryLifetime = TimeSpan.FromMinutes(2);

        private readonly object _gate = new();
        private readonly Func<string, bool> _onlineOnlyCloudFilesPlaceholderProbe;
        private readonly Func<string, bool> _pinnedCloudFilesPlaceholderProbe;
        private readonly Func<string, bool> _unpinnedCloudFilesPlaceholderProbe;
        private readonly LocalChangeSuppressionRegistry _registry;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalChangeSuppression" /> class.
        /// </summary>
        public LocalChangeSuppression(
            TimeSpan? entryLifetime = null,
            int eventBudget = 8,
            int maxEntriesPerPair = 100_000,
            TimeProvider? timeProvider = null)
            : this(
                LocalChangeSuppressionPath.IsOnlineOnlyPlaceholder,
                entryLifetime,
                eventBudget,
                maxEntriesPerPair,
                timeProvider,
                LocalChangeSuppressionPath.IsPinnedPlaceholder,
                LocalChangeSuppressionPath.IsUnpinnedPlaceholder)
        {
        }

        internal LocalChangeSuppression(
            Func<string, bool> onlineOnlyCloudFilesPlaceholderProbe,
            TimeSpan? entryLifetime = null,
            int eventBudget = 8,
            int maxEntriesPerPair = 100_000,
            TimeProvider? timeProvider = null,
            Func<string, bool>? pinnedCloudFilesPlaceholderProbe = null,
            Func<string, bool>? unpinnedCloudFilesPlaceholderProbe = null)
        {
            ArgumentNullException.ThrowIfNull(onlineOnlyCloudFilesPlaceholderProbe);
            TimeSpan normalizedEntryLifetime = entryLifetime ?? DefaultEntryLifetime;
            if (normalizedEntryLifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(entryLifetime), "Suppression lifetime must be positive.");
            }

            if (eventBudget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventBudget), "Suppression event budget must be positive.");
            }

            if (maxEntriesPerPair <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntriesPerPair), "Suppression capacity must be positive.");
            }

            _onlineOnlyCloudFilesPlaceholderProbe = onlineOnlyCloudFilesPlaceholderProbe;
            _pinnedCloudFilesPlaceholderProbe = pinnedCloudFilesPlaceholderProbe
                ?? LocalChangeSuppressionPath.IsPinnedPlaceholder;
            _unpinnedCloudFilesPlaceholderProbe = unpinnedCloudFilesPlaceholderProbe
                ?? LocalChangeSuppressionPath.IsUnpinnedPlaceholder;
            _registry = new LocalChangeSuppressionRegistry(
                normalizedEntryLifetime,
                eventBudget,
                maxEntriesPerPair,
                timeProvider ?? TimeProvider.System);
        }

        /// <inheritdoc />
        public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.None,
                suppressDeleteEvents: true,
                metadataOnly: false,
                creationOnly: false,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        /// <inheritdoc />
        public void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.Pinned,
                suppressDeleteEvents: false,
                metadataOnly: false,
                creationOnly: false,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        /// <inheritdoc />
        public void SuppressProviderDirectoryWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.Unpinned,
                suppressDeleteEvents: false,
                metadataOnly: false,
                creationOnly: false,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        /// <inheritdoc />
        public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.None,
                suppressDeleteEvents: false,
                metadataOnly: false,
                creationOnly: true,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        /// <inheritdoc />
        public void SuppressProviderFileMaterialization(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            long expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedSizeBytes);
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.None,
                suppressDeleteEvents: false,
                metadataOnly: false,
                creationOnly: true,
                expectedSizeBytes,
                expectedLastWriteUtc?.ToUniversalTime());
        }

        /// <inheritdoc />
        public void SuppressProviderMetadataWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.None,
                suppressDeleteEvents: false,
                metadataOnly: true,
                creationOnly: false,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        /// <inheritdoc />
        public void SuppressProviderOnlineOnlyWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                LocalChangeSuppressionAvailabilityCondition.OnlineOnly,
                suppressDeleteEvents: true,
                metadataOnly: false,
                creationOnly: false,
                expectedSizeBytes: null,
                expectedLastWriteUtc: null);
        }

        private void SuppressProviderWrite(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            LocalChangeSuppressionAvailabilityCondition availabilityCondition,
            bool suppressDeleteEvents,
            bool metadataOnly,
            bool creationOnly,
            long? expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id cannot be empty.", nameof(syncPairId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

            string rootPath = LocalChangeSuppressionPath.Normalize(localRootPath);
            string fullPath = LocalChangeSuppressionPath.ResolveInsideRoot(rootPath, relativePath);
            DateTimeOffset now = _registry.UtcNow;
            DateTimeOffset expiresAt = _registry.GetExpiration(now);

            lock (_gate)
            {
                Dictionary<string, SuppressionEntry> entries = _registry.GetEntries(syncPairId);

                _registry.Register(
                    syncPairId,
                    entries,
                    fullPath,
                    expiresAt,
                    availabilityCondition,
                    suppressDeleteEvents,
                    metadataOnly,
                    creationOnly,
                    expectedSizeBytes,
                    expectedLastWriteUtc);

                if (!creationOnly)
                {
                    string? currentPath = Path.GetDirectoryName(fullPath);
                    while (!string.IsNullOrWhiteSpace(currentPath)
                        && !LocalChangeSuppressionPath.PathEquals(rootPath, currentPath)
                        && LocalChangeSuppressionPath.IsInsideRoot(rootPath, currentPath))
                    {
                        _registry.Register(
                            syncPairId,
                            entries,
                            currentPath,
                            expiresAt,
                            availabilityCondition,
                            suppressDeleteEvents,
                            metadataOnly,
                            creationOnly: false,
                            expectedSizeBytes: null,
                            expectedLastWriteUtc: null);
                        currentPath = Path.GetDirectoryName(currentPath);
                    }
                }

                _registry.MaintainCapacity(syncPairId, entries, now);
            }
        }

        /// <inheritdoc />
        public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id cannot be empty.", nameof(syncPairId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            string rootPath = LocalChangeSuppressionPath.Normalize(localRootPath);
            lock (_gate)
            {
                _registry.BeginBurst(syncPairId, rootPath);
            }

            return new ProviderWriteBurstLease(EndProviderWriteBurst, syncPairId);
        }

        /// <inheritdoc />
        public bool ShouldSuppress(LocalSyncRootChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.Kind == LocalSyncRootChangeKind.Error)
            {
                return false;
            }

            DateTimeOffset now = _registry.UtcNow;
            lock (_gate)
            {
                if (!_registry.TryGetEntries(change.SyncPairId, out Dictionary<string, SuppressionEntry>? entries)
                    || entries is null)
                {
                    return ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: true);
                }

                bool providerCreationRename = change.Kind == LocalSyncRootChangeKind.Renamed
                    && HasActiveCreationOnlyEntry(change.FullPath, entries, now);
                bool suppressChangedPath = ShouldSuppressPath(
                    change.SyncPairId,
                    change.FullPath,
                    change.Kind,
                    entries,
                    now);
                bool suppressOldPath = true;
                if (!providerCreationRename && !string.IsNullOrWhiteSpace(change.OldFullPath))
                {
                    suppressOldPath = ShouldSuppressPath(
                        change.SyncPairId,
                        change.OldFullPath,
                        change.Kind,
                        entries,
                        now);
                }

                _registry.RemovePairIfEmpty(change.SyncPairId, entries);

                return (suppressChangedPath && suppressOldPath)
                    || ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: true);
            }
        }

        private static bool HasActiveCreationOnlyEntry(
            string fullPath,
            IReadOnlyDictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            string key = LocalChangeSuppressionPath.Normalize(fullPath);
            return entries.TryGetValue(key, out SuppressionEntry? entry)
                && entry.CreationOnly
                && entry.ExpiresAt > now
                && entry.RemainingEvents > 0;
        }

        private bool ShouldSuppressPath(
            Guid syncPairId,
            string fullPath,
            LocalSyncRootChangeKind changeKind,
            Dictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            string key = LocalChangeSuppressionPath.Normalize(fullPath);
            if (entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
                {
                    _registry.Remove(syncPairId, key, entries);
                    return false;
                }

                if (HasSuppressionEnded(syncPairId, fullPath, changeKind, entry))
                {
                    _registry.Remove(syncPairId, key, entries);
                    return false;
                }

                if (entry.CreationOnly && !entry.ExpectedSizeBytes.HasValue)
                {
                    return _registry.TryConsume(syncPairId, entries, fullPath, now);
                }
            }

            return _registry.ShouldSuppressRegisteredBurst(syncPairId, fullPath, entries, now)
                || _registry.TryConsume(syncPairId, entries, fullPath, now);
        }

        private bool HasSuppressionEnded(
            Guid syncPairId,
            string fullPath,
            LocalSyncRootChangeKind changeKind,
            SuppressionEntry entry)
        {
            return MustPreserveUserRemoval(changeKind, entry)
                || HasAvailabilityConditionEnded(syncPairId, fullPath, entry)
                || MustPreserveContentChange(changeKind, entry)
                || MustPreserveNonCreationChange(changeKind, entry)
                || HasProviderMaterializationChanged(fullPath, entry);
        }

        private static bool MustPreserveUserRemoval(
            LocalSyncRootChangeKind changeKind,
            SuppressionEntry entry)
        {
            return !entry.SuppressDeleteEvents
                && changeKind is LocalSyncRootChangeKind.Deleted or LocalSyncRootChangeKind.Renamed;
        }

        private static bool MustPreserveContentChange(
            LocalSyncRootChangeKind changeKind,
            SuppressionEntry entry)
        {
            return entry.MetadataOnly && changeKind != LocalSyncRootChangeKind.AttributesChanged;
        }

        private static bool MustPreserveNonCreationChange(
            LocalSyncRootChangeKind changeKind,
            SuppressionEntry entry)
        {
            return entry.CreationOnly
                && !entry.ExpectedSizeBytes.HasValue
                && changeKind is not LocalSyncRootChangeKind.Created and not LocalSyncRootChangeKind.Renamed;
        }

        private bool HasProviderMaterializationChanged(string fullPath, SuppressionEntry entry)
        {
            return entry.CreationOnly
                && entry.ExpectedSizeBytes.HasValue
                && !LocalChangeSuppressionPath.MatchesExpectedMetadata(fullPath, entry);
        }

        private bool ShouldSuppressProviderBurst(
            LocalSyncRootChange change,
            bool includeCloudFilesPlaceholderProbe)
        {
            return includeCloudFilesPlaceholderProbe
                && _registry.ShouldSuppressBurst(change, _onlineOnlyCloudFilesPlaceholderProbe);
        }

        private void EndProviderWriteBurst(Guid syncPairId)
        {
            lock (_gate)
            {
                _registry.EndBurst(syncPairId);
            }
        }

        internal static bool IsOnlineOnlyCloudFilesAttributes(FileAttributes attributes)
        {
            return LocalChangeSuppressionPath.IsOnlineOnlyAttributes(attributes);
        }

    }
}
