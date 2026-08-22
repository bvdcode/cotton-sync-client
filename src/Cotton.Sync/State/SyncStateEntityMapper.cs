// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    internal static class SyncStateEntityMapper
    {
        public static void Update(SyncStateEntity entity, SyncStateEntry entry, string key)
        {
            if (entry.LocalSizeBytes.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(entry.LocalSizeBytes.Value);
            }

            if (entry.RemoteSizeBytes.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(entry.RemoteSizeBytes.Value);
            }

            entity.SyncPairId = entry.SyncPairId;
            entity.RelativePathKey = key;
            entity.RelativePath = SyncPath.Normalize(entry.RelativePath);
            entity.Kind = entry.Kind;
            entity.LocalContentHash = NormalizeNullable(entry.LocalContentHash);
            entity.LocalLastWriteAt = ToUtc(entry.LocalLastWriteUtc);
            entity.LocalSizeBytes = entry.LocalSizeBytes;
            entity.RemoteSizeBytes = entry.RemoteSizeBytes;
            entity.RemoteNodeId = entry.RemoteNodeId;
            entity.RemoteFileId = entry.RemoteFileId;
            entity.RemoteFileManifestId = entry.RemoteFileManifestId;
            entity.RemoteOriginalNodeFileId = entry.RemoteOriginalNodeFileId;
            entity.RemoteContentHash = NormalizeNullable(entry.RemoteContentHash);
            entity.RemoteETag = NormalizeNullable(entry.RemoteETag);
            entity.PlaceholderIdentity = Clone(entry.PlaceholderIdentity);
            entity.PlaceholderHydrationState = entry.PlaceholderHydrationState;
            entity.SyncedAt = ToUtc(entry.SyncedAtUtc) ?? DateTime.UtcNow;
        }

        public static SyncStateEntry ToModel(SyncStateEntity entity)
        {
            return new SyncStateEntry
            {
                SyncPairId = entity.SyncPairId,
                RelativePath = entity.RelativePath,
                Kind = entity.Kind,
                LocalContentHash = entity.LocalContentHash,
                LocalLastWriteUtc = ToUtc(entity.LocalLastWriteAt),
                LocalSizeBytes = entity.LocalSizeBytes,
                RemoteSizeBytes = entity.RemoteSizeBytes,
                RemoteNodeId = entity.RemoteNodeId,
                RemoteFileId = entity.RemoteFileId,
                RemoteFileManifestId = entity.RemoteFileManifestId,
                RemoteOriginalNodeFileId = entity.RemoteOriginalNodeFileId,
                RemoteContentHash = entity.RemoteContentHash,
                RemoteETag = entity.RemoteETag,
                PlaceholderIdentity = Clone(entity.PlaceholderIdentity),
                PlaceholderHydrationState = entity.PlaceholderHydrationState,
                SyncedAtUtc = ToUtc(entity.SyncedAt) ?? DateTime.UtcNow,
            };
        }

        public static SyncChangeCursor CreateDefaultCursor(string syncPairId)
        {
            return new SyncChangeCursor
            {
                SyncPairId = syncPairId,
                LastCursor = 0,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        public static void Update(SyncChangeCursorEntity entity, SyncChangeCursor cursor)
        {
            entity.SyncPairId = cursor.SyncPairId;
            entity.LastCursor = cursor.LastCursor;
            entity.CursorExpired = cursor.CursorExpired;
            entity.EarliestAvailableCursor = cursor.EarliestAvailableCursor;
            entity.HasCompletedFullReconcile = cursor.HasCompletedFullReconcile;
            entity.CursorUpdatedAt = ToUtc(cursor.UpdatedAtUtc) ?? DateTime.UtcNow;
        }

        public static SyncChangeCursor ToModel(SyncChangeCursorEntity entity)
        {
            return new SyncChangeCursor
            {
                SyncPairId = entity.SyncPairId,
                LastCursor = entity.LastCursor,
                CursorExpired = entity.CursorExpired,
                EarliestAvailableCursor = entity.EarliestAvailableCursor,
                HasCompletedFullReconcile = entity.HasCompletedFullReconcile,
                UpdatedAtUtc = ToUtc(entity.CursorUpdatedAt) ?? DateTime.UtcNow,
            };
        }

        public static void Validate(SyncChangeCursor cursor)
        {
            if (cursor.LastCursor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor.LastCursor, "Change cursor cannot be negative.");
            }

            if (cursor.EarliestAvailableCursor < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cursor),
                    cursor.EarliestAvailableCursor,
                    "Earliest available cursor cannot be negative.");
            }
        }

        public static DateTime? ToUtc(DateTime? value)
        {
            return value?.Kind switch
            {
                null => null,
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => throw new ArgumentOutOfRangeException(nameof(value), value.Value.Kind, "Unsupported date time kind."),
            };
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static byte[]? Clone(byte[]? value)
        {
            return value is null ? null : (byte[])value.Clone();
        }
    }
}
