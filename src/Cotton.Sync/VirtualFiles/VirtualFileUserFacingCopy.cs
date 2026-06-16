// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Keeps Windows virtual-files wording consistent across GUI, CLI, and diagnostics.
    /// </summary>
    public static class VirtualFileUserFacingCopy
    {
        public const string FullMirrorModeLabel = "Full mirror";
        public const string WindowsVirtualFilesModeLabel = "Windows virtual files";
        public const string CreatingCloudFilesProgressLabel = "Making cloud files available";
        public const string PreparingCloudFilesProgressLabel = "Preparing cloud files";
        public const string CreatingCloudFilesCliStage = "making cloud files available";
        public const string CloudFilesProgressUnit = "cloud files";
        public const string RemoteOnlyLocalChangeRequiresActionMessage =
            "An online-only file was deleted or moved locally. Restore it from Cotton Sync or delete/rename it from Cotton web before syncing.";
        public const string CloudFilesPlaceholderFailedMessage =
            "Windows virtual files could not make a cloud file available in File Explorer. Check diagnostics and retry sync.";

        public static string GetHydrationStateLabel(SyncPlaceholderHydrationState state)
        {
            return state switch
            {
                SyncPlaceholderHydrationState.RemoteOnly => "Online-only",
                SyncPlaceholderHydrationState.Hydrated => "Available on this device",
                SyncPlaceholderHydrationState.Dehydrated => "Freed from this device",
                SyncPlaceholderHydrationState.HydrationFailed => "Needs attention",
                _ => "Not a virtual file",
            };
        }

        public static string GetHydrationStateDescription(SyncPlaceholderHydrationState state)
        {
            return state switch
            {
                SyncPlaceholderHydrationState.RemoteOnly => "Visible in File Explorer and downloads when opened.",
                SyncPlaceholderHydrationState.Hydrated => "Content is stored locally and stays synced.",
                SyncPlaceholderHydrationState.Dehydrated => "Visible in File Explorer and downloads again when opened.",
                SyncPlaceholderHydrationState.HydrationFailed => "Cotton Sync needs your review before virtual files can continue.",
                _ => "This item is not tracked as a Windows virtual file.",
            };
        }
    }
}
