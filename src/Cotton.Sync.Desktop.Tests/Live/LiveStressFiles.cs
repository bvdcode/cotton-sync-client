// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal static class LiveStressFiles
    {
        public static async Task<string> WriteAsync(string path, long length, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] buffer = new byte[1024 * 1024];
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream stream = new(path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous);
            while (length > 0)
            {
                int count = (int)Math.Min(length, buffer.Length);
                RandomNumberGenerator.Fill(buffer);
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                hash.AppendData(buffer, 0, count);
                length -= count;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        public static string FullPath(string root, string relativePath)
        {
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
