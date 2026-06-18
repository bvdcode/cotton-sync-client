// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopDiagnosticsPublicSanitizer
    {
        public static DesktopDiagnosticsBundle SanitizeBundle(
            DesktopAppPaths paths,
            DesktopDiagnosticsBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(bundle);
            IReadOnlyList<KnownValueReplacement> replacements = BuildKnownValueReplacements(paths, bundle);
            return bundle with
            {
                ServerUrl = bundle.ServerUrl is null ? null : "[server-url]",
                AccountName = NormalizeAccountName(bundle.AccountName),
                DataPaths = new DesktopDataPathSnapshot(
                    "[data-directory]",
                    "[app-database]",
                    "[sync-state-database]",
                    "[token-store]"),
                SyncPairs = bundle.SyncPairs
                    .Select((syncPair, index) => SanitizeSyncPair(syncPair, index))
                    .ToArray(),
                CloudFilesEvents = bundle.CloudFilesEvents
                    .Select(item => SanitizeCloudFilesEvent(item, replacements))
                    .ToArray(),
            };
        }

        public static string SanitizeText(
            string value,
            DesktopAppPaths paths,
            DesktopDiagnosticsBundle bundle)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            string result = DesktopSecretRedactor.Redact(value);
            foreach (KnownValueReplacement replacement in BuildKnownValueReplacements(paths, bundle))
            {
                result = result.Replace(
                    replacement.Value,
                    replacement.Placeholder,
                    StringComparison.OrdinalIgnoreCase);
            }

            return result;
        }

        private static DesktopSyncPairSnapshot SanitizeSyncPair(
            DesktopSyncPairSnapshot syncPair,
            int index)
        {
            int displayIndex = index + 1;
            return syncPair with
            {
                DisplayName = "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-name]",
                LocalPath = "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-local-root]",
                RemotePath = "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-remote-root]",
                RemoteRootNodeId = null,
                LastError = string.IsNullOrWhiteSpace(syncPair.LastError) ? syncPair.LastError : "[sync-pair-error]",
            };
        }

        private static WindowsCloudFilesDiagnosticEvent SanitizeCloudFilesEvent(
            WindowsCloudFilesDiagnosticEvent item,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            string? details = item.Details;
            if (!string.IsNullOrWhiteSpace(details))
            {
                foreach (KnownValueReplacement replacement in replacements)
                {
                    details = details.Replace(
                        replacement.Value,
                        replacement.Placeholder,
                        StringComparison.OrdinalIgnoreCase);
                }

                details = DesktopSecretRedactor.Redact(details);
            }

            return item with
            {
                SyncPairId = item.SyncPairId is null ? null : "[sync-pair-id]",
                LocalRootPath = item.LocalRootPath is null ? null : "[cloud-files-local-root]",
                RelativePath = item.RelativePath is null ? null : "[cloud-files-relative-path]",
                Details = details,
            };
        }

        private static string NormalizeAccountName(string accountName)
        {
            return string.Equals(accountName, "Signed out", StringComparison.OrdinalIgnoreCase)
                ? "Signed out"
                : "Signed in";
        }

        private static IReadOnlyList<KnownValueReplacement> BuildKnownValueReplacements(
            DesktopAppPaths paths,
            DesktopDiagnosticsBundle bundle)
        {
            var replacements = new List<KnownValueReplacement>
            {
                new(paths.DataDirectory, "[data-directory]"),
                new(paths.AppDatabasePath, "[app-database]"),
                new(paths.SyncStateDatabasePath, "[sync-state-database]"),
                new(paths.TokenStorePath, "[token-store]"),
                new(paths.SingleInstanceLockPath, "[single-instance-lock]"),
                new(paths.LogFilePath, "[log-file]"),
                new(paths.UpdateCacheDirectory, "[update-cache]"),
            };
            if (!string.IsNullOrWhiteSpace(bundle.ServerUrl))
            {
                replacements.Add(new KnownValueReplacement(bundle.ServerUrl, "[server-url]"));
            }

            if (!string.IsNullOrWhiteSpace(bundle.AccountName)
                && !string.Equals(bundle.AccountName, "Signed in", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(bundle.AccountName, "Signed out", StringComparison.OrdinalIgnoreCase))
            {
                replacements.Add(new KnownValueReplacement(bundle.AccountName, "[account]"));
            }

            for (int index = 0; index < bundle.SyncPairs.Count; index++)
            {
                DesktopSyncPairSnapshot syncPair = bundle.SyncPairs[index];
                int displayIndex = index + 1;
                AddIfUseful(replacements, syncPair.DisplayName, "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-name]");
                AddIfUseful(replacements, syncPair.LocalPath, "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-local-root]");
                AddIfUseful(replacements, syncPair.RemotePath, "[sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-remote-root]");
            }

            foreach (WindowsCloudFilesDiagnosticEvent item in bundle.CloudFilesEvents)
            {
                AddIfUseful(replacements, item.LocalRootPath, "[cloud-files-local-root]");
                AddIfUseful(replacements, item.RelativePath, "[cloud-files-relative-path]");
            }

            return replacements
                .Where(static item => !string.IsNullOrWhiteSpace(item.Value))
                .GroupBy(static item => item.Value, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderByDescending(static item => item.Value.Length)
                .ToArray();
        }

        private static void AddIfUseful(
            List<KnownValueReplacement> replacements,
            string? value,
            string placeholder)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
            {
                return;
            }

            replacements.Add(new KnownValueReplacement(value, placeholder));
        }

        private readonly record struct KnownValueReplacement(string Value, string Placeholder);
    }
}
