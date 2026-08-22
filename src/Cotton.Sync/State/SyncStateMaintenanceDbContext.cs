// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SyncStateMaintenanceDbContext : DbContext
    {
        public SyncStateMaintenanceDbContext(DbContextOptions<SyncStateMaintenanceDbContext> options)
            : base(options)
        {
        }

        public DbSet<SyncStateEntity> SyncEntries => Set<SyncStateEntity>();

        public DbSet<SyncChangeCursorEntity> SyncChangeCursors => Set<SyncChangeCursorEntity>();
    }
}
