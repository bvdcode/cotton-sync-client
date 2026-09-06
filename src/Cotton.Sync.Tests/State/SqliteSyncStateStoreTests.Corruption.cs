// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Microsoft.Data.Sqlite;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        [Test]
        public async Task InitializeAsync_RejectsInvalidDatabaseWithoutReplacingBytes()
        {
            const int notADatabaseErrorCode = 26;
            string databasePath = DatabasePath();
            byte[] originalBytes = "not a sqlite database"u8.ToArray();
            await File.WriteAllBytesAsync(databasePath, originalBytes);
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                SqliteException? exception = Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync());
                byte[] actualBytes = await File.ReadAllBytesAsync(databasePath);

                Assert.Multiple(() =>
                {
                    Assert.That(exception!.SqliteErrorCode, Is.EqualTo(notADatabaseErrorCode));
                    Assert.That(actualBytes, Is.EqualTo(originalBytes));
                });
            }

            SqliteException? readException = Assert.ThrowsAsync<SqliteException>(() => store.LoadPairAsync("pair-a"));
            byte[] bytesAfterRead = await File.ReadAllBytesAsync(databasePath);

            Assert.Multiple(() =>
            {
                Assert.That(readException!.SqliteErrorCode, Is.EqualTo(notADatabaseErrorCode));
                Assert.That(bytesAfterRead, Is.EqualTo(originalBytes));
            });
        }
    }
}
