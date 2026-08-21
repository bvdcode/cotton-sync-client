// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private static bool IsTrackedVirtualFile(SyncStateEntry? state)
        {
            return state is
            {
                Kind: SyncEntryKind.File,
                PlaceholderIdentity.Length: > 0,
            };
        }

        private static bool IsTrackedVirtualDirectory(SyncStateEntry? state)
        {
            return state is
            {
                Kind: SyncEntryKind.Directory,
                RemoteNodeId: not null,
            };
        }

        private static bool SizeMatchesBaseline(SyncStateEntry state, long localLength)
        {
            long? expectedLength = state.RemoteSizeBytes ?? state.LocalSizeBytes;
            return !expectedLength.HasValue || expectedLength.Value == localLength;
        }

        private static bool MaterializedBaselineMatches(
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState)
        {
            return state.PlaceholderHydrationState == SyncPlaceholderHydrationState.Hydrated
                && state.LocalSizeBytes == diskState.Length
                && state.LocalLastWriteUtc == diskState.LastWriteUtc
                && !string.IsNullOrWhiteSpace(state.LocalContentHash)
                && string.Equals(
                    state.LocalContentHash,
                    state.RemoteContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool IsUnchangedPinnedPlaceholder(
            SyncPairSettings syncPair,
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState)
        {
            if (!HasRawAttribute(diskState.Attributes, FileAttributePinned)
                || !MaterializedBaselineMatches(state, diskState))
            {
                return false;
            }

            WindowsCloudFilesPlaceholderState placeholderState = _cloudFiles.GetPlaceholderState(
                syncPair,
                state.RelativePath);
            return placeholderState.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder)
                && placeholderState.HasFlag(WindowsCloudFilesPlaceholderState.InSync);
        }

        private static bool IsManualFreeUpSpaceCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsCompletedManualFreeUpSpaceCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && (HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private static bool IsManualFreeUpSpaceDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributePinned);
        }

        private static bool IsManualPinRemovalDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && !HasRawAttribute(attributes, FileAttributePinned)
                && !HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsManualPinRemovalFileCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) == 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && !HasRawAttribute(attributes, FileAttributePinned)
                && !HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsCompletedOnDemandHydrationCandidate(
            SyncStateEntry state,
            FileAttributes attributes)
        {
            return (state.PlaceholderHydrationState is SyncPlaceholderHydrationState.RemoteOnly
                    or SyncPlaceholderHydrationState.Dehydrated)
                && IsManualPinRemovalFileCandidate(attributes);
        }

        private static bool IsManualAlwaysKeepCandidate(
            FileAttributes attributes,
            SyncPlaceholderHydrationState hydrationState)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributePinned)
                && (hydrationState != SyncPlaceholderHydrationState.Hydrated
                    || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private static bool IsManualAlwaysKeepDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributePinned);
        }

        private static bool IsHydrationComplete(
            FileAttributes attributes,
            SyncPlaceholderHydrationState hydrationState)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && hydrationState == SyncPlaceholderHydrationState.Hydrated
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool RequiresStartupAvailabilityRecovery(SyncRunCause causes)
        {
            return (causes & (SyncRunCause.Periodic | SyncRunCause.Resume)) != SyncRunCause.None;
        }

        private static bool RequiresLostAvailabilityRecovery(SyncRunCause causes)
        {
            return (causes & (SyncRunCause.LocalChangeOverflow | SyncRunCause.LocalWatcherError)) != SyncRunCause.None;
        }

        private static void AddAncestorDirectoryKeys(string relativePath, ISet<string> directoryKeys)
        {
            string ancestorPath = SyncPath.Normalize(relativePath);
            int separatorIndex = ancestorPath.LastIndexOf('/');
            while (separatorIndex > 0)
            {
                ancestorPath = ancestorPath[..separatorIndex];
                directoryKeys.Add(SyncPath.ToKey(ancestorPath));
                separatorIndex = ancestorPath.LastIndexOf('/');
            }
        }

        private static bool HasRawAttribute(FileAttributes attributes, int attribute)
        {
            return (((int)attributes) & attribute) == attribute;
        }

        private static bool IsHandledAvailabilityPath(
            string relativePath,
            IReadOnlySet<string> handledAvailabilityPathKeys)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            return handledAvailabilityPathKeys.Contains(SyncPath.ToKey(normalizedPath));
        }

        private static bool IsRootRelativePath(string relativePath)
        {
            string trimmed = relativePath.Trim();
            return trimmed == "." || trimmed == "/" || trimmed == "\\";
        }

        private static bool TryNormalizePath(string relativePath, out string normalizedPath)
        {
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
                return true;
            }
            catch (ArgumentException)
            {
                normalizedPath = string.Empty;
                return false;
            }
        }

        private WindowsVirtualFileDiskState? TryReadDiskState(string fullPath)
        {
            try
            {
                return _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static int GetPathDepth(string relativePath)
        {
            return relativePath.Count(static character => character == '/');
        }

        private static int GetAvailabilityPathDepth(string relativePath)
        {
            if (IsRootRelativePath(relativePath))
            {
                return -1;
            }

            return relativePath.Count(static character => character is '/' or '\\');
        }

        private static string ResolveFullPath(string localRootPath, string normalizedRelativePath)
        {
            string root = Path.GetFullPath(localRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(
                root,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Virtual file path escaped the sync root.", nameof(normalizedRelativePath));
            }

            return fullPath;
        }

        private static string? TryResolveFullPath(string localRootPath, string normalizedRelativePath)
        {
            try
            {
                return ResolveFullPath(localRootPath, normalizedRelativePath);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static WindowsVirtualFileDiskState? ReadDiskState(string fullPath)
        {
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                file.Refresh();
                return new WindowsVirtualFileDiskState(file.Attributes, file.Length, file.LastWriteTimeUtc);
            }

            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                directory.Refresh();
                return new WindowsVirtualFileDiskState(directory.Attributes, 0, directory.LastWriteTimeUtc);
            }

            return null;
        }
    }
}
