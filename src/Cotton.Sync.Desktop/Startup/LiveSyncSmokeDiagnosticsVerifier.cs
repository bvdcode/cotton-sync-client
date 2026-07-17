// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class LiveSyncSmokeDiagnosticsVerifier
    {
        public static LiveSyncSmokeDiagnosticsVerification Verify(string archivePath, Guid expectedSyncPairId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
            try
            {
                if (!File.Exists(archivePath))
                {
                    return LiveSyncSmokeDiagnosticsVerification.Failed("archiveMissing=true");
                }

                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                ZipArchiveEntry? diagnosticsEntry = archive.GetEntry("diagnostics.json");
                if (diagnosticsEntry is null)
                {
                    return LiveSyncSmokeDiagnosticsVerification.Failed("diagnosticsEntryMissing=true");
                }

                using Stream diagnosticsStream = diagnosticsEntry.Open();
                using JsonDocument document = JsonDocument.Parse(diagnosticsStream);
                JsonElement root = document.RootElement;
                string appVersion = root.GetProperty("appVersion").GetString() ?? string.Empty;
                string accountName = root.GetProperty("accountName").GetString() ?? string.Empty;
                bool isSignedIn = root.GetProperty("syncLifecycle").GetProperty("isSignedIn").GetBoolean();
                JsonElement syncPairs = root.GetProperty("syncPairs");
                JsonElement? expectedPair = syncPairs
                    .EnumerateArray()
                    .Cast<JsonElement?>()
                    .SingleOrDefault(pair => HasSyncPairId(pair, expectedSyncPairId));
                string pairStatus = expectedPair?.GetProperty("status").GetString() ?? string.Empty;
                bool hasPrivateEntry = archive.Entries.Any(static entry => IsPrivateEntryName(entry.FullName));
                bool passed = !string.IsNullOrWhiteSpace(appVersion)
                    && string.Equals(accountName, "Signed in", StringComparison.Ordinal)
                    && isSignedIn
                    && syncPairs.GetArrayLength() == 1
                    && expectedPair.HasValue
                    && string.Equals(pairStatus, "Idle", StringComparison.Ordinal)
                    && !hasPrivateEntry;
                string details = "appVersion=" + appVersion
                    + ", account=" + accountName
                    + ", signedIn=" + isSignedIn
                    + ", pairCount=" + syncPairs.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", pairStatus=" + pairStatus
                    + ", privateEntry=" + hasPrivateEntry;
                return new LiveSyncSmokeDiagnosticsVerification(passed, details);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                return LiveSyncSmokeDiagnosticsVerification.Failed(
                    "error=" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static bool HasSyncPairId(JsonElement? pair, Guid expectedSyncPairId)
        {
            if (!pair.HasValue
                || !pair.Value.TryGetProperty("id", out JsonElement idElement)
                || !Guid.TryParse(idElement.GetString(), out Guid syncPairId))
            {
                return false;
            }

            return syncPairId == expectedSyncPairId;
        }

        private static bool IsPrivateEntryName(string entryName)
        {
            return entryName.Contains("token", StringComparison.OrdinalIgnoreCase)
                || entryName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || entryName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal record LiveSyncSmokeDiagnosticsVerification(bool Passed, string Details)
    {
        public static LiveSyncSmokeDiagnosticsVerification Failed(string details)
        {
            return new LiveSyncSmokeDiagnosticsVerification(false, details);
        }
    }
}
