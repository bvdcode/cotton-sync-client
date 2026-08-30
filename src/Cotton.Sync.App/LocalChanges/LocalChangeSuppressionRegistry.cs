// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal class LocalChangeSuppressionRegistry
    {
        private readonly Dictionary<Guid, Dictionary<string, LocalChangeSuppressionEntry>> _entriesByPair = [];
        private readonly int _eventBudget;
        private readonly TimeSpan _entryLifetime;
        private readonly int _maxEntriesPerPair;
        private readonly Dictionary<Guid, ProviderWriteBurstScope> _providerWriteBurstsByPair = [];
        private readonly TimeProvider _timeProvider;
        private int _registrationCount;

        public LocalChangeSuppressionRegistry(
            TimeSpan entryLifetime,
            int eventBudget,
            int maxEntriesPerPair,
            TimeProvider timeProvider)
        {
            _entryLifetime = entryLifetime;
            _eventBudget = eventBudget;
            _maxEntriesPerPair = maxEntriesPerPair;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

        public DateTimeOffset GetExpiration(DateTimeOffset now)
        {
            return now.Add(_entryLifetime);
        }

        public Dictionary<string, LocalChangeSuppressionEntry> GetEntries(Guid syncPairId)
        {
            if (_entriesByPair.TryGetValue(
                syncPairId,
                out Dictionary<string, LocalChangeSuppressionEntry>? entries))
            {
                return entries;
            }

            entries = new Dictionary<string, LocalChangeSuppressionEntry>(StringComparer.OrdinalIgnoreCase);
            _entriesByPair[syncPairId] = entries;
            return entries;
        }

        public bool TryGetEntries(
            Guid syncPairId,
            out Dictionary<string, LocalChangeSuppressionEntry>? entries)
        {
            return _entriesByPair.TryGetValue(syncPairId, out entries);
        }

        public void RemovePairIfEmpty(Guid syncPairId, IReadOnlyDictionary<string, LocalChangeSuppressionEntry> entries)
        {
            if (entries.Count == 0)
            {
                _entriesByPair.Remove(syncPairId);
            }
        }

        public void Register(
            Guid syncPairId,
            Dictionary<string, LocalChangeSuppressionEntry> entries,
            string fullPath,
            DateTimeOffset expiresAt,
            bool onlyWhileOnlineOnly,
            bool onlyWhilePinned,
            bool suppressDeleteEvents,
            bool metadataOnly,
            bool creationOnly,
            long? expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            string key = LocalChangeSuppressionPath.Normalize(fullPath);
            if (_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope)
                && scope.ActiveCount > 0)
            {
                scope.RegisteredPathKeys.Add(key);
            }

            if (entries.TryGetValue(key, out LocalChangeSuppressionEntry? entry))
            {
                entry.ExpiresAt = expiresAt;
                entry.RemainingEvents = Math.Min(entry.RemainingEvents + _eventBudget, _eventBudget * 16);
                entry.OnlyWhileOnlineOnly = onlyWhileOnlineOnly;
                entry.OnlyWhilePinned = onlyWhilePinned;
                entry.SuppressDeleteEvents = suppressDeleteEvents;
                entry.MetadataOnly = metadataOnly;
                entry.CreationOnly = creationOnly;
                entry.ExpectedSizeBytes = expectedSizeBytes;
                entry.ExpectedLastWriteUtc = expectedLastWriteUtc;
                return;
            }

            entries.Add(key, new LocalChangeSuppressionEntry(
                expiresAt,
                _eventBudget,
                onlyWhileOnlineOnly,
                onlyWhilePinned,
                suppressDeleteEvents,
                metadataOnly,
                creationOnly,
                expectedSizeBytes,
                expectedLastWriteUtc));
        }

        public void MaintainCapacity(
            Guid syncPairId,
            Dictionary<string, LocalChangeSuppressionEntry> entries,
            DateTimeOffset now)
        {
            if ((++_registrationCount & 0x1ff) == 0)
            {
                PruneExpired(syncPairId, entries, now);
            }

            if (entries.Count <= _maxEntriesPerPair)
            {
                return;
            }

            PruneExpired(syncPairId, entries, now);
            int removeCount = entries.Count - _maxEntriesPerPair;
            foreach (string key in entries
                         .OrderBy(static pair => pair.Value.ExpiresAt)
                         .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(removeCount)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                Remove(syncPairId, key, entries);
            }
        }

        public void Remove(
            Guid syncPairId,
            string key,
            Dictionary<string, LocalChangeSuppressionEntry> entries)
        {
            entries.Remove(key);
            if (_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope))
            {
                scope.RegisteredPathKeys.Remove(key);
            }
        }

        public bool TryConsume(
            Guid syncPairId,
            Dictionary<string, LocalChangeSuppressionEntry> entries,
            string fullPath,
            DateTimeOffset now)
        {
            string key = LocalChangeSuppressionPath.Normalize(fullPath);
            if (!entries.TryGetValue(key, out LocalChangeSuppressionEntry? entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
            {
                Remove(syncPairId, key, entries);
                return false;
            }

            entry.RemainingEvents--;
            if (entry.RemainingEvents == 0)
            {
                Remove(syncPairId, key, entries);
            }

            return true;
        }

        public void BeginBurst(Guid syncPairId, string rootPath)
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
                return;
            }

            _providerWriteBurstsByPair[syncPairId] = new ProviderWriteBurstScope(rootPath);
        }

        public void EndBurst(Guid syncPairId)
        {
            if (!_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope))
            {
                return;
            }

            scope.ActiveCount--;
            if (scope.ActiveCount > 0)
            {
                return;
            }

            scope.ActiveCount = 0;
            DateTimeOffset now = UtcNow;
            DateTimeOffset expiresAt = GetExpiration(now);
            scope.ExpiresAt = expiresAt;
            if (_entriesByPair.TryGetValue(
                syncPairId,
                out Dictionary<string, LocalChangeSuppressionEntry>? entries))
            {
                foreach (string key in scope.RegisteredPathKeys.ToArray())
                {
                    if (entries.TryGetValue(key, out LocalChangeSuppressionEntry? entry)
                        && entry.ExpiresAt > now
                        && entry.RemainingEvents > 0)
                    {
                        entry.ExpiresAt = expiresAt;
                    }
                    else
                    {
                        Remove(syncPairId, key, entries);
                    }
                }
            }

            scope.RegisteredPathKeys.Clear();
        }

        public bool ShouldSuppressRegisteredBurst(
            Guid syncPairId,
            string fullPath,
            Dictionary<string, LocalChangeSuppressionEntry> entries,
            DateTimeOffset now)
        {
            if (!_providerWriteBurstsByPair.TryGetValue(syncPairId, out ProviderWriteBurstScope? scope)
                || scope.ActiveCount <= 0
                || !LocalChangeSuppressionPath.IsInsideRoot(scope.RootPath, fullPath))
            {
                return false;
            }

            string key = LocalChangeSuppressionPath.Normalize(fullPath);
            if (!scope.RegisteredPathKeys.Contains(key)
                || !entries.TryGetValue(key, out LocalChangeSuppressionEntry? entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= now || entry.RemainingEvents <= 0)
            {
                Remove(syncPairId, key, entries);
                return false;
            }

            return TryConsume(syncPairId, entries, fullPath, now);
        }

        public bool ShouldSuppressBurst(
            LocalSyncRootChange change,
            Func<string, bool> onlineOnlyPlaceholderProbe)
        {
            if (!_providerWriteBurstsByPair.TryGetValue(change.SyncPairId, out ProviderWriteBurstScope? scope)
                || !LocalChangeSuppressionPath.IsInsideRoot(scope.RootPath, change.FullPath))
            {
                return false;
            }

            if (scope.ActiveCount <= 0 && scope.ExpiresAt <= UtcNow)
            {
                _providerWriteBurstsByPair.Remove(change.SyncPairId);
                return false;
            }

            return onlineOnlyPlaceholderProbe(change.FullPath)
                && (string.IsNullOrWhiteSpace(change.OldFullPath)
                    || onlineOnlyPlaceholderProbe(change.OldFullPath));
        }

        private void PruneExpired(
            Guid syncPairId,
            Dictionary<string, LocalChangeSuppressionEntry> entries,
            DateTimeOffset now)
        {
            foreach (string key in entries
                         .Where(pair => pair.Value.ExpiresAt <= now || pair.Value.RemainingEvents <= 0)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                Remove(syncPairId, key, entries);
            }
        }
    }
}
