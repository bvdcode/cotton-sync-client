// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cotton.Sync.TestSupport
{
    internal static class HistoricalMigrationDataWriter
    {
        public static async Task InsertAsync(
            DbContext context,
            string table,
            string[] columns,
            string[] columnTypes,
            object?[] values,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(table);
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentNullException.ThrowIfNull(columnTypes);
            ArgumentNullException.ThrowIfNull(values);

            string providerName = context.Database.ProviderName
                ?? throw new InvalidOperationException("The database provider is unavailable.");
            MigrationBuilder migrationBuilder = new(providerName);
            migrationBuilder.InsertData(table, columns, columnTypes, values);

            IMigrationsSqlGenerator generator = context.GetService<IMigrationsSqlGenerator>();
            IReadOnlyList<MigrationCommand> commands = generator.Generate(migrationBuilder.Operations);
            IRelationalConnection connection = context.GetService<IRelationalConnection>();
            bool opened = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (MigrationCommand command in commands)
                {
                    await command.ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
