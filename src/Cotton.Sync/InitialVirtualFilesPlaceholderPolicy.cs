// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal static class InitialVirtualFilesPlaceholderPolicy
    {
        public static bool HasRemoteBaseline(SyncStateEntry entry)
        {
            return entry.Kind == SyncEntryKind.File
                && (!string.IsNullOrWhiteSpace(entry.RemoteContentHash)
                    || !string.IsNullOrWhiteSpace(entry.RemoteETag)
                    || entry.RemoteFileId.HasValue);
        }

        public static bool HasRemoteBaseline(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return !string.IsNullOrWhiteSpace(baseline.RemoteContentHash)
                || !string.IsNullOrWhiteSpace(baseline.RemoteETag)
                || baseline.RemoteFileId.HasValue;
        }

        public static bool IsOnlineOnly(SyncStateEntry entry)
        {
            return entry.Kind == SyncEntryKind.File
                && IsOnlineOnlyHydrationState(entry.PlaceholderHydrationState)
                && entry.PlaceholderIdentity is { Length: > 0 };
        }

        public static bool IsOnlineOnly(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return IsOnlineOnlyHydrationState(baseline.PlaceholderHydrationState)
                && baseline.HasPlaceholderIdentity;
        }

        public static bool IsResumeCandidate(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            bool hasSupportedHydrationState = baseline.PlaceholderHydrationState is
                SyncPlaceholderHydrationState.RemoteOnly
                or SyncPlaceholderHydrationState.Hydrated
                or SyncPlaceholderHydrationState.Dehydrated;
            return hasSupportedHydrationState && baseline.HasPlaceholderIdentity;
        }

        public static bool IsResumeCompatible(
            LocalFileSnapshot local,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            if (!local.IsCloudFilesPlaceholder
                || !IsResumeCandidate(baseline)
                || !baseline.RemoteFileId.HasValue)
            {
                return false;
            }

            if (baseline.PlaceholderHydrationState != SyncPlaceholderHydrationState.Hydrated)
            {
                return local.IsCloudFilesOnlineOnlyPlaceholder;
            }

            return !local.IsCloudFilesOnlineOnlyPlaceholder
                && !string.IsNullOrWhiteSpace(baseline.LocalContentHash)
                && baseline.LocalSizeBytes.HasValue
                && baseline.LocalSizeBytes.Value == local.SizeBytes
                && baseline.LocalLastWriteUtc.HasValue
                && baseline.LocalLastWriteUtc.Value.ToUniversalTime() == local.LastWriteUtc.ToUniversalTime();
        }

        private static bool IsOnlineOnlyHydrationState(SyncPlaceholderHydrationState hydrationState)
        {
            return hydrationState is SyncPlaceholderHydrationState.RemoteOnly or SyncPlaceholderHydrationState.Dehydrated;
        }
    }
}
