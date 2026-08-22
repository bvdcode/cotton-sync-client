// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.App.State.Migrations
{
    /// <inheritdoc />
    public partial class AdoptBaseEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "updated_at_utc",
                table: "sync_pair_settings",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "created_at_utc",
                table: "sync_pair_settings",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "updated_at_utc",
                table: "app_preferences",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "created_at_utc",
                table: "app_preferences",
                newName: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "sync_pair_settings",
                newName: "updated_at_utc");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "sync_pair_settings",
                newName: "created_at_utc");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "app_preferences",
                newName: "updated_at_utc");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "app_preferences",
                newName: "created_at_utc");
        }
    }
}
