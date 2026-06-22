// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cotton.Sync.App.State.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeLegacySyncPairMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sync_pair_settings SET mode = 1 WHERE mode = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
