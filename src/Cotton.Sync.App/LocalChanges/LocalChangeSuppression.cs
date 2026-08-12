// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Suppresses short-lived filesystem watcher echoes produced by provider-side virtual file work.
    /// </summary>
    public class LocalChangeSuppression : ILocalChangeSuppression
    {
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;
        private const int FileAttributePinned = 0x00080000;
        private static readonly TimeSpan DefaultEntryLifetime = TimeSpan.FromMinutes(2);
        private static readonly char[] DirectorySeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        private readonly object _gate = new();
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _entryLifetime;
        private readonly int _eventBudget;
        private readonly int _maxEntriesPerPair;
        private readonly Func<string, bool> _onlineOnlyCloudFilesPlaceholderProbe;
        private readonly Dictionary<Guid, Dictionary<string, SuppressionEntry>> _entriesByPair = [];
        private readonly Dictionary<Guid, ProviderWriteBurstScope> _providerWriteBurstsByPair = [];
        private int _registrationCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalChangeSuppression" /> class.
        /// </summary>
        public LocalChangeSuppression(
            TimeSpan? entryLifetime = null,
            int eventBudget = 8,
            int maxEntriesPerPair = 100_000,
            TimeProvider? timeProvider = null)
            : this(
                IsOnlineOnlyCloudFilesPlaceholder,
                entryLifetime,
                eventBudget,
                maxEntriesPerPair,
                timeProvider)
        {
        }

        internal LocalChangeSuppression(
            Func<string, bool> onlineOnlyCloudFilesPlaceholderProbe,
            TimeSpan? entryLifetime = null,
            int eventBudget = 8,
            int maxEntriesPerPair = 100_000,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(onlineOnlyCloudFilesPlaceholderProbe);
            _entryLifetime = entryLifetime ?? DefaultEntryLifetime;
            if (_entryLifetime <= TimeSpan.Zero)
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

            _eventBudget = eventBudget;
            _maxEntriesPerPair = maxEntriesPerPair;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _onlineOnlyCloudFilesPlaceholderProbe = onlineOnlyCloudFilesPlaceholderProbe;
        }

        /// <inheritdoc />
        public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(
                syncPairId,
                localRootPath,
                relativePath,
                onlyWhileOnlineOnly: false,
                suppressDeleteEvents: true,
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
                onlyWhileOnlineOnly: false,
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
                onlyWhileOnlineOnly: false,
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
                onlyWhileOnlineOnly: false,
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
                onlyWhileOnlineOnly: true,
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
            bool onlyWhileOnlineOnly,
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

            string rootPath = NormalizePathKey(localRootPath);
            string fullPath = ResolveInsideRoot(rootPath, relativePath);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset expiresAt = now.Add(_entryLifetime);

            lock (_gate)
            {
                Dictionary<string, SuppressionEntry> entries = GetOrCreatePairEntries(syncPairId);
                if ((++_registrationCount & 0x1ff) == 0)
                {
                    PruneExpired(syncPairId, entries, now);
                }

                Register(
                    syncPairId,
                    entries,
                    fullPath,
                    expiresAt,
                    onlyWhileOnlineOnly,
                    suppressDeleteEvents,
                    metadataOnly,
                    creationOnly,
                    expectedSizeBytes,
                    expectedLastWriteUtc);

                if (!creationOnly)
                {
                    string? currentPath = Path.GetDirectoryName(fullPath);
                    while (!string.IsNullOrWhiteSpace(currentPath)
                        && !PathEquals(rootPath, currentPath)
                        && IsInsideRoot(rootPath, currentPath))
                    {
                        Register(
                            syncPairId,
                            entries,
                            currentPath,
                            expiresAt,
                            onlyWhileOnlineOnly,
                            suppressDeleteEvents,
                            metadataOnly,
                            creationOnly: false,
                            expectedSizeBytes: null,
                            expectedLastWriteUtc: null);
                        currentPath = Path.GetDirectoryName(currentPath);
                    }
                }

                if (entries.Count > _maxEntriesPerPair)
                {
                    PruneExpired(syncPairId, entries, now);
                    TrimCapacity(syncPairId, entries);
                }
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
            string rootPath = NormalizePathKey(localRootPath);
            lock (_gate)
            {
                if (_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope))
                {
                    if (scope.ActiveCount <= 0)
                    {
                        scope.RegisteredPathKeys.Clear();
                    }

                    scope.ActiveCount++;
                    scope.RootPath = rootPath;
                    scope.ExpiresAt = DateTimeOffset.MaxValue;
                }
                else
                {
                    _providerWriteBurstsByPair[syncPairId] = new ProviderWriteBurstScope(rootPath);
                }
            }

            return new ProviderWriteBurstLease(this, syncPairId);
        }

        /// <inheritdoc />
        public bool ShouldSuppress(LocalSyncRootChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.Kind == LocalSyncRootChangeKind.Error)
            {
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                if (!_entriesByPair.TryGetValue(change.SyncPairId, out Dictionary<string, SuppressionEntry>? entries))
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

                if (entries.Count == 0)
                {
                    _entriesByPair.Remove(change.SyncPairId);
                }

                return (suppressChangedPath && suppressOldPath)
                    || ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: true);
            }
        }

        private static bool HasActiveCreationOnlyEntry(
            string fullPath,
            IReadOnlyDictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            string key = NormalizePathKey(fullPath);
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
            string key = NormalizePathKey(fullPath);
            if (entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
                {
                    RemoveSuppressionEntry(syncPairId, key, entries);
                    return false;
                }

                if (HasSuppressionEnded(fullPath, changeKind, entry))
                {
                    RemoveSuppressionEntry(syncPairId, key, entries);
                    return false;
                }

                if (entry.CreationOnly && !entry.ExpectedSizeBytes.HasValue)
                {
                    return TryConsume(syncPairId, entries, fullPath, now);
                }
            }

            return ShouldSuppressRegisteredProviderBurst(syncPairId, fullPath, entries, now)
                || TryConsume(syncPairId, entries, fullPath, now);
        }

        private bool HasSuppressionEnded(
            string fullPath,
            LocalSyncRootChangeKind changeKind,
            SuppressionEntry entry)
        {
            return MustPreserveUserRemoval(changeKind, entry)
                || HasOnlineOnlyConditionEnded(fullPath, entry)
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

        private bool HasOnlineOnlyConditionEnded(string fullPath, SuppressionEntry entry)
        {
            return entry.OnlyWhileOnlineOnly && !_onlineOnlyCloudFilesPlaceholderProbe(fullPath);
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
                && !MatchesExpectedFileMetadata(fullPath, entry);
        }

        private void RemoveSuppressionEntry(
            Guid syncPairId,
            string key,
            Dictionary<string, SuppressionEntry> entries)
        {
            entries.Remove(key);
            if (_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope))
            {
                scope.RegisteredPathKeys.Remove(key);
            }
        }

        private bool ShouldSuppressRegisteredProviderBurst(
            Guid syncPairId,
            string fullPath,
            Dictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            if (!_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope)
                || scope.ActiveCount <= 0
                || !IsInsideRoot(scope.RootPath, fullPath))
            {
                return false;
            }

            string key = NormalizePathKey(fullPath);
            if (!scope.RegisteredPathKeys.Contains(key)
                || !entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
            {
                RemoveSuppressionEntry(syncPairId, key, entries);
                return false;
            }

            return TryConsume(syncPairId, entries, fullPath, now);
        }

        private void EndProviderWriteBurst(Guid syncPairId)
        {
            lock (_gate)
            {
                if (!_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope))
                {
                    return;
                }

                scope.ActiveCount--;
                if (scope.ActiveCount <= 0)
                {
                    scope.ActiveCount = 0;
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    DateTimeOffset expiresAt = now.Add(_entryLifetime);
                    scope.ExpiresAt = expiresAt;
                    if (_entriesByPair.TryGetValue(syncPairId, out Dictionary<string, SuppressionEntry>? entries))
                    {
                        foreach (string key in scope.RegisteredPathKeys.ToArray())
                        {
                            if (entries.TryGetValue(key, out SuppressionEntry? entry)
                                && entry.ExpiresAt > now
                                && entry.RemainingEvents > 0)
                            {
                                entry.ExpiresAt = expiresAt;
                            }
                            else
                            {
                                RemoveSuppressionEntry(syncPairId, key, entries);
                            }
                        }
                    }

                    scope.RegisteredPathKeys.Clear();
                }
            }
        }

        private Dictionary<string, SuppressionEntry> GetOrCreatePairEntries(Guid syncPairId)
        {
            if (_entriesByPair.TryGetValue(syncPairId, out Dictionary<string, SuppressionEntry>? entries))
            {
                return entries;
            }

            entries = new Dictionary<string, SuppressionEntry>(StringComparer.OrdinalIgnoreCase);
            _entriesByPair[syncPairId] = entries;
            return entries;
        }

        private void Register(
            Guid syncPairId,
            Dictionary<string, SuppressionEntry> entries,
            string fullPath,
            DateTimeOffset expiresAt,
            bool onlyWhileOnlineOnly,
            bool suppressDeleteEvents,
            bool metadataOnly,
            bool creationOnly,
            long? expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            string key = NormalizePathKey(fullPath);
            if (_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope)
                && scope.ActiveCount > 0)
            {
                scope.RegisteredPathKeys.Add(key);
            }

            if (entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                entry.ExpiresAt = expiresAt;
                entry.RemainingEvents = Math.Min(entry.RemainingEvents + _eventBudget, _eventBudget * 16);
                entry.OnlyWhileOnlineOnly = onlyWhileOnlineOnly;
                entry.SuppressDeleteEvents = suppressDeleteEvents;
                entry.MetadataOnly = metadataOnly;
                entry.CreationOnly = creationOnly;
                entry.ExpectedSizeBytes = expectedSizeBytes;
                entry.ExpectedLastWriteUtc = expectedLastWriteUtc;
                return;
            }

            entries.Add(key, new SuppressionEntry(
                expiresAt,
                _eventBudget,
                onlyWhileOnlineOnly,
                suppressDeleteEvents,
                metadataOnly,
                creationOnly,
                expectedSizeBytes,
                expectedLastWriteUtc));
        }

        private static bool MatchesExpectedFileMetadata(string fullPath, SuppressionEntry entry)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length != entry.ExpectedSizeBytes)
                {
                    return false;
                }

                return !entry.ExpectedLastWriteUtc.HasValue
                    || info.LastWriteTimeUtc == entry.ExpectedLastWriteUtc.Value;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return false;
            }
        }

        private bool TryConsume(
            Guid syncPairId,
            Dictionary<string, SuppressionEntry> entries,
            string fullPath,
            DateTimeOffset now)
        {
            string key = NormalizePathKey(fullPath);
            if (!entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
            {
                RemoveSuppressionEntry(syncPairId, key, entries);
                return false;
            }

            entry.RemainingEvents--;
            if (entry.RemainingEvents == 0)
            {
                RemoveSuppressionEntry(syncPairId, key, entries);
            }

            return true;
        }

        private void PruneExpired(
            Guid syncPairId,
            Dictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            foreach (string key in entries
                         .Where(pair => pair.Value.ExpiresAt <= now || pair.Value.RemainingEvents <= 0)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                RemoveSuppressionEntry(syncPairId, key, entries);
            }
        }

        private void TrimCapacity(
            Guid syncPairId,
            Dictionary<string, SuppressionEntry> entries)
        {
            int removeCount = entries.Count - _maxEntriesPerPair;
            if (removeCount <= 0)
            {
                return;
            }

            foreach (string key in entries
                         .OrderBy(static pair => pair.Value.ExpiresAt)
                         .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(removeCount)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                RemoveSuppressionEntry(syncPairId, key, entries);
            }
        }

        private bool ShouldSuppressProviderBurst(
            LocalSyncRootChange change,
            bool includeCloudFilesPlaceholderProbe)
        {
            if (!_providerWriteBurstsByPair.TryGetValue(change.SyncPairId, out ProviderWriteBurstScope? scope)
                || !IsInsideRoot(scope.RootPath, change.FullPath))
            {
                return false;
            }

            if (scope.ActiveCount <= 0 && scope.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _providerWriteBurstsByPair.Remove(change.SyncPairId);
                return false;
            }

            if (!includeCloudFilesPlaceholderProbe
                || !_onlineOnlyCloudFilesPlaceholderProbe(change.FullPath))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(change.OldFullPath)
                || _onlineOnlyCloudFilesPlaceholderProbe(change.OldFullPath);
        }

        private static bool IsOnlineOnlyCloudFilesPlaceholder(string fullPath)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(fullPath);
                return IsOnlineOnlyCloudFilesAttributes(attributes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool HasRawAttribute(FileAttributes attributes, int rawAttribute)
        {
            return (((int)attributes) & rawAttribute) == rawAttribute;
        }

        internal static bool IsOnlineOnlyCloudFilesAttributes(FileAttributes attributes)
        {
            return !HasRawAttribute(attributes, FileAttributePinned)
                && (HasRawAttribute(attributes, FileAttributeRecallOnOpen)
                || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                || (attributes & FileAttributes.Offline) != 0);
        }

        private static string ResolveInsideRoot(string localRootPath, string relativePath)
        {
            string normalizedRelativePath = SyncPath.Normalize(relativePath);
            string localRelativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = NormalizePathKey(Path.Combine(localRootPath, localRelativePath));
            if (!IsInsideRoot(localRootPath, fullPath))
            {
                throw new ArgumentException("Suppression path must stay inside the local sync root.", nameof(relativePath));
            }

            return fullPath;
        }

        private static bool IsInsideRoot(string localRootPath, string fullPath)
        {
            string normalizedRoot = NormalizePathKey(localRootPath);
            string normalizedPath = NormalizePathKey(fullPath);
            string rootWithSeparator = normalizedRoot.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar;
            return PathEquals(normalizedRoot, normalizedPath)
                || normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(
                NormalizePathKey(left),
                NormalizePathKey(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePathKey(string fullPath)
        {
            string normalized = Path.GetFullPath(fullPath);
            string? root = Path.GetPathRoot(normalized);
            if (!string.IsNullOrEmpty(root) && PathEqualsRoot(normalized, root))
            {
                return root;
            }

            return normalized.TrimEnd(DirectorySeparators);
        }

        private static bool PathEqualsRoot(string fullPath, string root)
        {
            string trimmedFullPath = fullPath.TrimEnd(DirectorySeparators);
            string trimmedRoot = root.TrimEnd(DirectorySeparators);
            return string.Equals(trimmedFullPath, trimmedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private class SuppressionEntry
        {
            public SuppressionEntry(
                DateTimeOffset expiresAt,
                int remainingEvents,
                bool onlyWhileOnlineOnly,
                bool suppressDeleteEvents,
                bool metadataOnly,
                bool creationOnly,
                long? expectedSizeBytes,
                DateTime? expectedLastWriteUtc)
            {
                ExpiresAt = expiresAt;
                RemainingEvents = remainingEvents;
                OnlyWhileOnlineOnly = onlyWhileOnlineOnly;
                SuppressDeleteEvents = suppressDeleteEvents;
                MetadataOnly = metadataOnly;
                CreationOnly = creationOnly;
                ExpectedSizeBytes = expectedSizeBytes;
                ExpectedLastWriteUtc = expectedLastWriteUtc;
            }

            public DateTimeOffset ExpiresAt { get; set; }

            public int RemainingEvents { get; set; }

            public bool OnlyWhileOnlineOnly { get; set; }

            public bool SuppressDeleteEvents { get; set; }

            public bool MetadataOnly { get; set; }

            public bool CreationOnly { get; set; }

            public long? ExpectedSizeBytes { get; set; }

            public DateTime? ExpectedLastWriteUtc { get; set; }
        }

        private class ProviderWriteBurstScope
        {
            public ProviderWriteBurstScope(string rootPath)
            {
                RootPath = rootPath;
                ActiveCount = 1;
                ExpiresAt = DateTimeOffset.MaxValue;
            }

            public string RootPath { get; set; }

            public int ActiveCount { get; set; }

            public DateTimeOffset ExpiresAt { get; set; }

            public HashSet<string> RegisteredPathKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private class ProviderWriteBurstLease : IDisposable
        {
            private LocalChangeSuppression? _owner;
            private readonly Guid _syncPairId;

            public ProviderWriteBurstLease(LocalChangeSuppression owner, Guid syncPairId)
            {
                _owner = owner;
                _syncPairId = syncPairId;
            }

            public void Dispose()
            {
                LocalChangeSuppression? owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndProviderWriteBurst(_syncPairId);
            }
        }
    }
}
