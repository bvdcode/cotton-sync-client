// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SqliteSyncStateDiagnosticsReader(
        SyncStateDbContextFactory contextFactory,
        SyncStateStoreInitializer initializer)
    {
        public async Task<SyncStateStoreDiagnostics> GetAsync(CancellationToken cancellationToken)
        {
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            string fullPath = Path.GetFullPath(contextFactory.DatabasePath);
            long syncEntryCount;
            long syncChangeCursorCount;
            await using (SyncStateDbContext context = contextFactory.Create())
            {
                syncEntryCount = await context.SyncEntries.LongCountAsync(cancellationToken).ConfigureAwait(false);
                syncChangeCursorCount = await context.SyncChangeCursors
                    .LongCountAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            SqlitePageUsage pageUsage = await SqliteDatabaseHeaderReader
                .ReadAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            long fileSizeBytes = new FileInfo(fullPath).Length;
            return new SyncStateStoreDiagnostics(
                fileSizeBytes,
                pageUsage.PageCount,
                pageUsage.FreelistCount,
                pageUsage.PageSize,
                syncEntryCount,
                syncChangeCursorCount);
        }
    }
}
