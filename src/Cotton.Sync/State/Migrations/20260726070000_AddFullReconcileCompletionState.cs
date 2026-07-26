// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.State.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SyncStateDbContext))]
    [Migration("20260726070000_AddFullReconcileCompletionState")]
    public sealed class AddFullReconcileCompletionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_completed_full_reconcile",
                table: "sync_change_cursors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "has_completed_full_reconcile",
                table: "sync_change_cursors");
        }
    }
}
