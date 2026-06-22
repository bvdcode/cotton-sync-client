// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
                SelfTestItems = bundle.SelfTestItems
                    .Select(item => SanitizeSelfTestItem(item, replacements))
                    .ToArray(),
                Auth = SanitizeAuth(bundle.Auth, replacements),
                Update = SanitizeUpdate(bundle.Update, replacements),
                CloudFilesRegistration = SanitizeCloudFilesRegistration(bundle.CloudFilesRegistration, replacements),
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

        private static DesktopSelfTestItemSnapshot SanitizeSelfTestItem(
            DesktopSelfTestItemSnapshot item,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            string details = item.Details;
            foreach (KnownValueReplacement replacement in replacements)
            {
                details = details.Replace(
                    replacement.Value,
                    replacement.Placeholder,
                    StringComparison.OrdinalIgnoreCase);
            }

            return item with { Details = DesktopSecretRedactor.Redact(details) };
        }

        private static DesktopUpdateDiagnosticsSnapshot SanitizeUpdate(
            DesktopUpdateDiagnosticsSnapshot update,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            string? failureMessage = update.FailureMessage;
            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                foreach (KnownValueReplacement replacement in replacements)
                {
                    failureMessage = failureMessage.Replace(
                        replacement.Value,
                        replacement.Placeholder,
                        StringComparison.OrdinalIgnoreCase);
                }

                failureMessage = DesktopSecretRedactor.Redact(failureMessage);
            }

            return update with { FailureMessage = failureMessage };
        }

        private static DesktopAuthDiagnosticsSnapshot SanitizeAuth(
            DesktopAuthDiagnosticsSnapshot auth,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            string? failureMessage = auth.LastSessionRestoreFailureMessage;
            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                foreach (KnownValueReplacement replacement in replacements)
                {
                    failureMessage = failureMessage.Replace(
                        replacement.Value,
                        replacement.Placeholder,
                        StringComparison.OrdinalIgnoreCase);
                }

                failureMessage = DesktopSecretRedactor.Redact(failureMessage);
            }

            return auth with { LastSessionRestoreFailureMessage = failureMessage };
        }

        private static DesktopCloudFilesRegistrationDiagnosticsSnapshot SanitizeCloudFilesRegistration(
            DesktopCloudFilesRegistrationDiagnosticsSnapshot registration,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            return registration with
            {
                SyncPairs = registration.SyncPairs
                    .Select((pair, index) => SanitizeCloudFilesRegistrationPair(pair, index, replacements))
                    .ToArray(),
            };
        }

        private static DesktopCloudFilesSyncPairRegistrationSnapshot SanitizeCloudFilesRegistrationPair(
            DesktopCloudFilesSyncPairRegistrationSnapshot pair,
            int index,
            IReadOnlyList<KnownValueReplacement> replacements)
        {
            int displayIndex = index + 1;
            string details = pair.Details;
            foreach (KnownValueReplacement replacement in replacements)
            {
                details = details.Replace(
                    replacement.Value,
                    replacement.Placeholder,
                    StringComparison.OrdinalIgnoreCase);
            }

            return pair with
            {
                SyncPairId = Guid.Empty,
                DisplayName = "[cloud-files-sync-pair-"
                    + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "-name]",
                LocalRootPath = "[cloud-files-sync-pair-"
                    + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "-local-root]",
                Details = DesktopSecretRedactor.Redact(details),
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
                new(AppContext.BaseDirectory, "[app-base-directory]"),
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

            for (int index = 0; index < bundle.CloudFilesRegistration.SyncPairs.Count; index++)
            {
                DesktopCloudFilesSyncPairRegistrationSnapshot pair = bundle.CloudFilesRegistration.SyncPairs[index];
                int displayIndex = index + 1;
                AddIfUseful(replacements, pair.DisplayName, "[cloud-files-sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-name]");
                AddIfUseful(replacements, pair.LocalRootPath, "[cloud-files-sync-pair-" + displayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-local-root]");
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
