// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal class LocalChangeSuppressionEntry
    {
        public LocalChangeSuppressionEntry(
            DateTimeOffset expiresAt,
            int remainingEvents,
            bool onlyWhileOnlineOnly,
            bool onlyWhilePinned,
            bool suppressDeleteEvents,
            bool metadataOnly,
            bool creationOnly,
            long? expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            ExpiresAt = expiresAt;
            RemainingEvents = remainingEvents;
            OnlyWhileOnlineOnly = onlyWhileOnlineOnly;
            OnlyWhilePinned = onlyWhilePinned;
            SuppressDeleteEvents = suppressDeleteEvents;
            MetadataOnly = metadataOnly;
            CreationOnly = creationOnly;
            ExpectedSizeBytes = expectedSizeBytes;
            ExpectedLastWriteUtc = expectedLastWriteUtc;
        }

        public DateTimeOffset ExpiresAt { get; set; }

        public int RemainingEvents { get; set; }

        public bool OnlyWhileOnlineOnly { get; set; }

        public bool OnlyWhilePinned { get; set; }

        public bool SuppressDeleteEvents { get; set; }

        public bool MetadataOnly { get; set; }

        public bool CreationOnly { get; set; }

        public long? ExpectedSizeBytes { get; set; }

        public DateTime? ExpectedLastWriteUtc { get; set; }
    }
}
