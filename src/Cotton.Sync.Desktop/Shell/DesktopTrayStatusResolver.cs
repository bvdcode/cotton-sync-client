// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal static class DesktopTrayStatusResolver
    {
        private const string ToolTipPrefix = "Cotton Sync";
        private const int MaximumToolTipLength = 127;

        public static DesktopTrayStatus FromShellState(
            bool isSignedIn,
            string statusText,
            bool hasStatusAttention,
            bool hasActiveSyncProgress = false,
            string? activeProgressTitle = null,
            string? activeProgressDetails = null,
            string? activeProgressHeaderDetails = null,
            DesktopTrayActivityKind activeActivityKind = DesktopTrayActivityKind.None)
        {
            if (!isSignedIn)
            {
                return Create(DesktopTrayStatusKind.SignedOut, "Signed out");
            }

            if (hasStatusAttention || Contains(statusText, "action") || Contains(statusText, "failed"))
            {
                return Create(
                    DesktopTrayStatusKind.Error,
                    Contains(statusText, "conflict") ? "Conflicts need review" : "Action required");
            }

            if (Contains(statusText, "offline"))
            {
                return Create(DesktopTrayStatusKind.Offline, "Offline");
            }

            if (Contains(statusText, "paused"))
            {
                return Create(DesktopTrayStatusKind.Paused, "Paused");
            }

            if (hasActiveSyncProgress)
            {
                return Create(
                    ResolveActiveStatusKind(activeActivityKind),
                    CreateActiveSyncLabel(activeProgressTitle, activeProgressDetails, activeProgressHeaderDetails));
            }

            return Create(DesktopTrayStatusKind.Idle, "Connected");
        }

        private static string CreateActiveSyncLabel(
            string? activeProgressTitle,
            string? activeProgressDetails,
            string? activeProgressHeaderDetails)
        {
            string title = string.IsNullOrWhiteSpace(activeProgressTitle) ? "Syncing" : activeProgressTitle.Trim();
            string details = string.IsNullOrWhiteSpace(activeProgressDetails)
                ? string.Empty
                : activeProgressDetails.Trim();
            string headerDetails = string.IsNullOrWhiteSpace(activeProgressHeaderDetails)
                ? string.Empty
                : activeProgressHeaderDetails.Trim();
            string label = string.IsNullOrWhiteSpace(details) ? title : title + " - " + details;
            return string.IsNullOrWhiteSpace(headerDetails) ? label : label + " - " + headerDetails;
        }

        private static DesktopTrayStatusKind ResolveActiveStatusKind(DesktopTrayActivityKind activityKind)
        {
            return activityKind switch
            {
                DesktopTrayActivityKind.None => DesktopTrayStatusKind.Syncing,
                DesktopTrayActivityKind.Syncing => DesktopTrayStatusKind.Syncing,
                DesktopTrayActivityKind.Uploading => DesktopTrayStatusKind.Uploading,
                DesktopTrayActivityKind.Downloading => DesktopTrayStatusKind.Downloading,
                DesktopTrayActivityKind.MakingAvailable => DesktopTrayStatusKind.Downloading,
                DesktopTrayActivityKind.FreeingSpace => DesktopTrayStatusKind.FreeingSpace,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(activityKind),
                    activityKind,
                    "Unknown tray activity cannot be resolved."),
            };
        }

        private static DesktopTrayStatus Create(DesktopTrayStatusKind kind, string label)
        {
            string toolTip = ToolTipPrefix + " - " + label;
            if (toolTip.Length > MaximumToolTipLength)
            {
                toolTip = toolTip[..(MaximumToolTipLength - 3)] + "...";
            }

            return new DesktopTrayStatus(kind, toolTip, DesktopTrayIconAssetResolver.Resolve(kind));
        }

        private static bool Contains(string value, string expected)
        {
            return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
