// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Buffers.Binary;
using System.Text;

namespace Cotton.Sync.State
{
    internal static class SqliteDatabaseHeaderReader
    {
        private const int DatabaseSizePageCountOffset = 28;
        private const int FreelistPageCountOffset = 36;
        private const int HeaderSize = 100;
        private const int MaximumPageSize = 65_536;
        private const int MaximumPageSizeMarker = 1;
        private const int PageSizeOffset = 16;
        private const string HeaderSignature = "SQLite format 3\0";

        public static async Task<SqlitePageUsage> ReadAsync(
            string databasePath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            await using FileStream stream = new(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                HeaderSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] header = new byte[HeaderSize];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            ValidateSignature(header);

            int encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(PageSizeOffset, sizeof(ushort)));
            long pageSize = encodedPageSize == MaximumPageSizeMarker ? MaximumPageSize : encodedPageSize;
            if (pageSize <= 0)
            {
                throw new InvalidDataException("The SQLite database header contains an invalid page size.");
            }

            long pageCount = BinaryPrimitives.ReadUInt32BigEndian(
                header.AsSpan(DatabaseSizePageCountOffset, sizeof(uint)));
            if (pageCount == 0)
            {
                pageCount = (stream.Length + pageSize - 1) / pageSize;
            }

            long freelistCount = BinaryPrimitives.ReadUInt32BigEndian(
                header.AsSpan(FreelistPageCountOffset, sizeof(uint)));
            return new SqlitePageUsage(pageCount, freelistCount, pageSize);
        }

        private static void ValidateSignature(ReadOnlySpan<byte> header)
        {
            ReadOnlySpan<byte> expected = Encoding.ASCII.GetBytes(HeaderSignature);
            if (!header[..expected.Length].SequenceEqual(expected))
            {
                throw new InvalidDataException("The sync state file is not a valid SQLite database.");
            }
        }
    }
}
