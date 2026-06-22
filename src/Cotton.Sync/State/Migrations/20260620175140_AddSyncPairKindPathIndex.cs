// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.State.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncPairKindPathIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_sync_entries_sync_pair_id_kind_relative_path_key",
                table: "sync_entries",
                columns: new[] { "sync_pair_id", "kind", "relative_path_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sync_entries_sync_pair_id_kind_relative_path_key",
                table: "sync_entries");
        }
    }
}
