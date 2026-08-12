// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal static class RemoteDeletePlanFingerprint
    {
        private const int Sha256HexLength = 64;

        public static string Create(IEnumerable<string> planItems)
        {
            ArgumentNullException.ThrowIfNull(planItems);
            string canonicalPlan = string.Join(
                '\n',
                planItems
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static item => item, StringComparer.Ordinal));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPlan)));
        }

        public static string CreateFileItem(string relativePath, Guid? remoteFileId)
        {
            return CreateItem("file", relativePath, remoteFileId);
        }

        public static string CreateDirectoryItem(string relativePath, Guid? remoteNodeId)
        {
            return CreateItem("directory", relativePath, remoteNodeId);
        }

        public static bool IsValid(string? fingerprint)
        {
            return fingerprint is { Length: Sha256HexLength }
                && fingerprint.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        private static string CreateItem(string kind, string relativePath, Guid? remoteId)
        {
            return kind
                + "\0"
                + SyncPath.ToKey(relativePath)
                + "\0"
                + remoteId?.ToString("D");
        }
    }
}
