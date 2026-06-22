// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    /// <summary>
    /// Captures SQLite sync-state storage usage without exposing local filesystem paths.
    /// </summary>
    public sealed record SyncStateStoreDiagnostics(
        long FileSizeBytes,
        long PageCount,
        long FreelistCount,
        long PageSizeBytes,
        long SyncEntryCount,
        long SyncChangeCursorCount)
    {
        public long UsedPageCount => Math.Max(0, PageCount - FreelistCount);

        public long UsedBytes => SafeMultiply(UsedPageCount, PageSizeBytes);

        public long FreelistBytes => SafeMultiply(FreelistCount, PageSizeBytes);

        public double FreelistRatio => PageCount <= 0 ? 0 : FreelistCount / (double)PageCount;

        public bool HasRows => SyncEntryCount > 0 || SyncChangeCursorCount > 0;

        private static long SafeMultiply(long left, long right)
        {
            if (left <= 0 || right <= 0)
            {
                return 0;
            }

            return left > long.MaxValue / right ? long.MaxValue : left * right;
        }
    }
}
