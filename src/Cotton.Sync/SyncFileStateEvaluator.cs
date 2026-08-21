// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal static class SyncFileStateEvaluator
    {
        private static readonly TimeSpan CloudFilesMetadataTimestampTolerance = TimeSpan.FromSeconds(2);
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static bool RemoteMatchesBaseline(NodeFileManifestDto remoteFile, SyncStateEntry state)
        {
            if (!string.IsNullOrWhiteSpace(state.RemoteContentHash))
            {
                return ContentMatches(remoteFile.ContentHash, state.RemoteContentHash);
            }

            if (!string.IsNullOrWhiteSpace(state.RemoteETag))
            {
                return string.Equals(remoteFile.ETag, state.RemoteETag, StringComparison.Ordinal);
            }

            return state.RemoteFileId.HasValue && remoteFile.Id == state.RemoteFileId.Value;
        }

        public static bool RemoteMatchesBaseline(
            NodeFileManifestDto remoteFile,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            if (!string.IsNullOrWhiteSpace(baseline.RemoteContentHash))
            {
                return ContentMatches(remoteFile.ContentHash, baseline.RemoteContentHash);
            }

            if (!string.IsNullOrWhiteSpace(baseline.RemoteETag))
            {
                return string.Equals(remoteFile.ETag, baseline.RemoteETag, StringComparison.Ordinal);
            }

            return baseline.RemoteFileId.HasValue && remoteFile.Id == baseline.RemoteFileId.Value;
        }

        public static bool BaselineMatchesCurrentFile(
            SyncPair syncPair,
            string relativePath,
            SyncStateEntry state,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile)
        {
            return state.Kind == SyncEntryKind.File
                && string.Equals(state.SyncPairId, syncPair.SyncPairId, StringComparison.Ordinal)
                && PathComparer.Equals(SyncPath.ToKey(state.RelativePath), SyncPath.ToKey(relativePath))
                && ContentMatches(state.LocalContentHash, local.ContentHash)
                && NullableUtcEquals(state.LocalLastWriteUtc, local.LastWriteUtc)
                && state.LocalSizeBytes == local.SizeBytes
                && state.RemoteFileId == remoteFile.Id
                && state.RemoteNodeId == remoteFile.NodeId
                && ContentMatches(state.RemoteContentHash, remoteFile.ContentHash)
                && string.Equals(state.RemoteETag, remoteFile.ETag, StringComparison.Ordinal);
        }

        public static bool DateTimesMatchWithinCloudFilesMetadataTolerance(DateTime left, DateTime right)
        {
            TimeSpan difference = left.ToUniversalTime() - right.ToUniversalTime();
            return difference.Duration() <= CloudFilesMetadataTimestampTolerance;
        }

        public static bool IsMissingOnlineOnlyPlaceholder(
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is null && remote is not null && IsOnlineOnlyPlaceholderState(state);
        }

        public static bool LocalAndRemoteContentMatches(
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is not null
                && remote is not null
                && ContentMatches(local.ContentHash, remote.File.ContentHash);
        }

        public static SyncFileChangeState CreateFileChangeState(
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return new SyncFileChangeState(
                LocalDeleted: local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash),
                RemoteDeleted: remote is null && state.RemoteFileId.HasValue,
                LocalChanged: local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash),
                RemoteChanged: remote is not null && !RemoteMatchesBaseline(remote.File, state),
                BaselineDiverged: !ContentMatches(state.LocalContentHash, state.RemoteContentHash));
        }

        public static SyncFileChangeKind ResolveTrackedFileChange(SyncFileChangeState changeState)
        {
            if (changeState.BaselineDiverged)
            {
                return changeState.HasChanges ? SyncFileChangeKind.Conflict : SyncFileChangeKind.None;
            }

            return (changeState.LocalDeleted, changeState.RemoteDeleted, changeState.LocalChanged, changeState.RemoteChanged) switch
            {
                (false, false, false, false) => SyncFileChangeKind.None,
                (true, true, false, false) => SyncFileChangeKind.DeleteState,
                (false, true, false, false) => SyncFileChangeKind.DeleteLocal,
                (true, false, false, false) => SyncFileChangeKind.DeleteRemote,
                (false, false, true, false) => SyncFileChangeKind.Upload,
                (false, false, false, true) => SyncFileChangeKind.Download,
                _ => SyncFileChangeKind.Conflict,
            };
        }

        public static bool HasMissingRemoteOnlyPlaceholder(
            SyncPair syncPair,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return false;
            }

            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (IsOnlineOnlyPlaceholderState(state.Value)
                    && !localByPath.ContainsKey(state.Key)
                    && remoteByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsOnlineOnlyPlaceholderBaseline(SyncPair syncPair, SyncStateEntry state)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && IsOnlineOnlyPlaceholderState(state);
        }

        public static bool IsOnlineOnlyPlaceholderBaseline(
            SyncPair syncPair,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && IsOnlineOnlyPlaceholderState(baseline);
        }

        public static bool IsLocalOnlineOnlyPlaceholderBaseline(
            SyncPair syncPair,
            LocalFileSnapshot local,
            SyncStateEntry state)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder
                && IsOnlineOnlyPlaceholderBaseline(syncPair, state);
        }

        public static bool IsOnlineOnlyPlaceholderState(SyncStateEntry state)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsOnlineOnly(state);
        }

        public static bool IsIncompleteOnlineOnlyPlaceholderBaseline(SyncStateEntry state)
        {
            return state.Kind == SyncEntryKind.File
                && (state.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                    || state.PlaceholderHydrationState == SyncPlaceholderHydrationState.Dehydrated)
                && state.PlaceholderIdentity is not { Length: > 0 }
                && HasRemoteFileBaseline(state);
        }

        public static bool HasRemoteFileBaseline(SyncStateEntry state)
        {
            return InitialVirtualFilesPlaceholderPolicy.HasRemoteBaseline(state);
        }

        public static bool IsOnlineOnlyPlaceholderState(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsOnlineOnly(baseline);
        }

        public static bool IsVirtualFilesResumeCandidateState(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsResumeCandidate(baseline);
        }

        public static bool HasRemoteFileBaseline(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.HasRemoteBaseline(baseline);
        }

        public static bool ContentMatches(string? left, string? right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldFinalizeConvergedLocalFile(SyncPair syncPair, LocalFileSnapshot local)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && !local.IsCloudFilesOnlineOnlyPlaceholder;
        }

        private static bool NullableUtcEquals(DateTime? left, DateTime? right)
        {
            return left?.ToUniversalTime() == right?.ToUniversalTime();
        }
    }
}
