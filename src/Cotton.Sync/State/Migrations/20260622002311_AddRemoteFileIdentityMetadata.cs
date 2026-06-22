// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.State.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteFileIdentityMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "remote_file_manifest_id",
                table: "sync_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "remote_original_node_file_id",
                table: "sync_entries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "remote_file_manifest_id",
                table: "sync_entries");

            migrationBuilder.DropColumn(
                name: "remote_original_node_file_id",
                table: "sync_entries");
        }
    }
}
