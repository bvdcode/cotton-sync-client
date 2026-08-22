// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.State.Migrations
{
    /// <inheritdoc />
    public partial class AdoptBaseEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_sync_change_cursors",
                table: "sync_change_cursors");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "sync_entries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "sync_entries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "id",
                table: "sync_change_cursors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "sync_change_cursors",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "sync_change_cursors",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddPrimaryKey(
                name: "PK_sync_change_cursors",
                table: "sync_change_cursors",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_change_cursors_sync_pair_id",
                table: "sync_change_cursors",
                column: "sync_pair_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_sync_change_cursors",
                table: "sync_change_cursors");

            migrationBuilder.DropIndex(
                name: "IX_sync_change_cursors_sync_pair_id",
                table: "sync_change_cursors");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sync_entries");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sync_entries");

            migrationBuilder.DropColumn(
                name: "id",
                table: "sync_change_cursors");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sync_change_cursors");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "sync_change_cursors");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sync_change_cursors",
                table: "sync_change_cursors",
                column: "sync_pair_id");
        }
    }
}
