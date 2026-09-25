// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using System.Security.Cryptography;

namespace Cotton.Sync.Cli.Tests.TestSupport
{
    internal static class SyncTestContentManifestFactory
    {
        public static FileContentManifestDto Create(Guid nodeFileId, byte[] content, int? chunkSizeBytes = null)
        {
            int chunkSize = chunkSizeBytes ?? Math.Max(1, content.Length);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            List<FileContentManifestChunkDto> chunks = [];
            for (int offset = 0; offset < content.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, content.Length - offset);
                string chunkHash = Convert.ToHexStringLower(SHA256.HashData(content.AsSpan(offset, length)));
                chunks.Add(new FileContentManifestChunkDto
                {
                    Index = chunks.Count,
                    Offset = offset,
                    Length = length,
                    Hash = chunkHash,
                    ChunkId = chunkHash,
                });
            }

            return new FileContentManifestDto
            {
                NodeFileId = nodeFileId,
                FileManifestId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                ContentHash = contentHash,
                ETag = "sha256-" + contentHash,
                SizeBytes = content.Length,
                ChunkSizeBytes = chunkSize,
                Chunks = chunks,
            };
        }
    }
}
