// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.State.Migrations
{
    /// <inheritdoc />
    public partial class AddVirtualFileStateMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "placeholder_hydration_state",
                table: "sync_entries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "placeholder_identity",
                table: "sync_entries",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "remote_size_bytes",
                table: "sync_entries",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "placeholder_hydration_state",
                table: "sync_entries");

            migrationBuilder.DropColumn(
                name: "placeholder_identity",
                table: "sync_entries");

            migrationBuilder.DropColumn(
                name: "remote_size_bytes",
                table: "sync_entries");
        }
    }
}
