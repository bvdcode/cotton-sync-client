// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Suppresses short-lived filesystem watcher echoes produced by provider-side virtual file work.
    /// </summary>
    public sealed class LocalChangeSuppression : ILocalChangeSuppression
    {
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;
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
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id cannot be empty.", nameof(syncPairId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

            string rootPath = NormalizePathKey(localRootPath);
            string fullPath = ResolveInsideRoot(rootPath, relativePath);
            DateTimeOffset expiresAt = _timeProvider.GetUtcNow().Add(_entryLifetime);

            lock (_gate)
            {
                Dictionary<string, SuppressionEntry> entries = GetOrCreatePairEntries(syncPairId);
                if ((++_registrationCount & 0x1ff) == 0)
                {
                    PruneExpired(entries, _timeProvider.GetUtcNow());
                }

                Register(entries, fullPath, expiresAt);

                string? currentPath = Path.GetDirectoryName(fullPath);
                while (!string.IsNullOrWhiteSpace(currentPath)
                    && !PathEquals(rootPath, currentPath)
                    && IsInsideRoot(rootPath, currentPath))
                {
                    Register(entries, currentPath, expiresAt);
                    currentPath = Path.GetDirectoryName(currentPath);
                }

                if (entries.Count > _maxEntriesPerPair)
                {
                    PruneExpired(entries, _timeProvider.GetUtcNow());
                    TrimCapacity(entries);
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
                lock (_gate)
                {
                    return ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: false);
                }
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                if (!_entriesByPair.TryGetValue(change.SyncPairId, out Dictionary<string, SuppressionEntry>? entries))
                {
                    return ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: true);
                }

                bool suppress = TryConsume(entries, change.FullPath, now);
                if (!string.IsNullOrWhiteSpace(change.OldFullPath))
                {
                    suppress |= TryConsume(entries, change.OldFullPath, now);
                }

                if (entries.Count == 0)
                {
                    _entriesByPair.Remove(change.SyncPairId);
                }

                return suppress || ShouldSuppressProviderBurst(change, includeCloudFilesPlaceholderProbe: true);
            }
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
                    scope.ExpiresAt = _timeProvider.GetUtcNow().Add(_entryLifetime);
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
            Dictionary<string, SuppressionEntry> entries,
            string fullPath,
            DateTimeOffset expiresAt)
        {
            string key = NormalizePathKey(fullPath);
            if (entries.TryGetValue(key, out SuppressionEntry? entry))
            {
                entry.ExpiresAt = expiresAt;
                entry.RemainingEvents = Math.Min(entry.RemainingEvents + _eventBudget, _eventBudget * 16);
                return;
            }

            entries.Add(key, new SuppressionEntry(expiresAt, _eventBudget));
        }

        private bool TryConsume(
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
                entries.Remove(key);
                return false;
            }

            entry.RemainingEvents--;
            if (entry.RemainingEvents == 0)
            {
                entries.Remove(key);
            }

            return true;
        }

        private static void PruneExpired(
            Dictionary<string, SuppressionEntry> entries,
            DateTimeOffset now)
        {
            foreach (string key in entries
                         .Where(pair => pair.Value.ExpiresAt <= now || pair.Value.RemainingEvents <= 0)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                entries.Remove(key);
            }
        }

        private void TrimCapacity(Dictionary<string, SuppressionEntry> entries)
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
                entries.Remove(key);
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

            if (change.Kind == LocalSyncRootChangeKind.Error)
            {
                return scope.ActiveCount > 0;
            }

            return includeCloudFilesPlaceholderProbe
                && _onlineOnlyCloudFilesPlaceholderProbe(change.FullPath);
        }

        private static bool IsOnlineOnlyCloudFilesPlaceholder(string fullPath)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(fullPath);
                return HasRawAttribute(attributes, FileAttributeUnpinned)
                    && HasRawAttribute(attributes, FileAttributeRecallOnDataAccess);
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

        private sealed class SuppressionEntry
        {
            public SuppressionEntry(DateTimeOffset expiresAt, int remainingEvents)
            {
                ExpiresAt = expiresAt;
                RemainingEvents = remainingEvents;
            }

            public DateTimeOffset ExpiresAt { get; set; }

            public int RemainingEvents { get; set; }
        }

        private sealed class ProviderWriteBurstScope
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
        }

        private sealed class ProviderWriteBurstLease : IDisposable
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
