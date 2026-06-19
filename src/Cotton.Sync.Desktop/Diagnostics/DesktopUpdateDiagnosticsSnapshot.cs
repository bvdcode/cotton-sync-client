// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopUpdateDiagnosticsSnapshot(
        string CurrentVersion,
        bool IsUpdateCacheDirectoryPresent,
        bool HasPendingUpdate,
        string? PendingVersion,
        long? PendingInstallerSizeBytes,
        DateTimeOffset? LastCheckAtUtc,
        string LastCheckStatus,
        string? LastCheckSource,
        string? LatestVersion,
        bool? IsUpdateAvailable,
        bool? HasInstallerAsset,
        bool? IsInstallerReady,
        Uri? ReleaseUrl,
        string? FailureType,
        string? FailureMessage)
    {
        public static DesktopUpdateDiagnosticsSnapshot NotChecked(string currentVersion)
        {
            return new DesktopUpdateDiagnosticsSnapshot(
                currentVersion,
                IsUpdateCacheDirectoryPresent: false,
                HasPendingUpdate: false,
                PendingVersion: null,
                PendingInstallerSizeBytes: null,
                LastCheckAtUtc: null,
                LastCheckStatus: "not-checked",
                LastCheckSource: null,
                LatestVersion: null,
                IsUpdateAvailable: null,
                HasInstallerAsset: null,
                IsInstallerReady: null,
                ReleaseUrl: null,
                FailureType: null,
                FailureMessage: null);
        }

        public static DesktopUpdateDiagnosticsSnapshot FromCheck(
            string source,
            DesktopUpdateCheckResult check,
            string? installerPath,
            DateTimeOffset checkedAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentNullException.ThrowIfNull(check);
            return new DesktopUpdateDiagnosticsSnapshot(
                check.CurrentVersion.ToString(),
                IsUpdateCacheDirectoryPresent: false,
                HasPendingUpdate: false,
                PendingVersion: null,
                PendingInstallerSizeBytes: null,
                checkedAtUtc,
                LastCheckStatus: "succeeded",
                LastCheckSource: source,
                LatestVersion: check.LatestVersion.ToString(),
                IsUpdateAvailable: check.IsUpdateAvailable,
                HasInstallerAsset: check.InstallerAsset is not null,
                IsInstallerReady: !string.IsNullOrWhiteSpace(installerPath),
                ReleaseUrl: check.Manifest.ReleaseUrl,
                FailureType: null,
                FailureMessage: null);
        }

        public static DesktopUpdateDiagnosticsSnapshot FromFailure(
            string source,
            string currentVersion,
            Exception exception,
            DateTimeOffset checkedAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
            ArgumentNullException.ThrowIfNull(exception);
            return new DesktopUpdateDiagnosticsSnapshot(
                currentVersion,
                IsUpdateCacheDirectoryPresent: false,
                HasPendingUpdate: false,
                PendingVersion: null,
                PendingInstallerSizeBytes: null,
                checkedAtUtc,
                LastCheckStatus: "failed",
                LastCheckSource: source,
                LatestVersion: null,
                IsUpdateAvailable: null,
                HasInstallerAsset: null,
                IsInstallerReady: null,
                ReleaseUrl: null,
                exception.GetType().Name,
                CleanFailureMessage(exception.Message));
        }

        private static string? CleanFailureMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return DesktopSecretRedactor.Redact(message.ReplaceLineEndings(" ")).Trim();
        }
    }
}
