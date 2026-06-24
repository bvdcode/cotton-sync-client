// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsVirtualFilesRootCleaner : IWindowsVirtualFilesRootCleaner
    {
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;
        private static readonly TimeSpan[] CleanupRetryDelays =
        [
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
        ];

        private static readonly EnumerationOptions ChildEnumeration = new()
        {
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
        };

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesAdapter? _cloudFiles;
        private readonly IReadOnlySet<string> _knownCloudFilesRelativePathKeys;

        public WindowsVirtualFilesRootCleaner(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesAdapter? cloudFiles = null,
            IEnumerable<string>? knownCloudFilesRelativePaths = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _cloudFiles = cloudFiles;
            _knownCloudFilesRelativePathKeys = (knownCloudFilesRelativePaths ?? [])
                .Select(SyncPath.ToKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public WindowsVirtualFilesRootCleanupDecision EvaluateBeforeUnregister(SyncPairSettings syncPair)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(syncPair.LocalRootPath);
            if (!safety.IsSafe)
            {
                return Skip(syncPair.LocalRootPath, safety.Details);
            }

            string rootPath = safety.FullPath;
            if (!Directory.Exists(rootPath))
            {
                return Skip(rootPath, "Local root is already absent.");
            }

            string? inspectionFailure = InspectForRemoval(syncPair, rootPath);
            if (inspectionFailure is not null)
            {
                return Skip(rootPath, inspectionFailure);
            }

            return new WindowsVirtualFilesRootCleanupDecision(
                rootPath,
                ShouldRemoveRoot: true,
                "Local root contains only directories and Cloud Files placeholders.");
        }

        public async Task<WindowsVirtualFilesRootCleanupResult> CleanupAfterUnregisterAsync(
            WindowsVirtualFilesRootCleanupDecision decision,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(decision);
            cancellationToken.ThrowIfCancellationRequested();
            if (!decision.ShouldRemoveRoot)
            {
                return new WindowsVirtualFilesRootCleanupResult(false, decision.Reason);
            }

            for (int attempt = 0; attempt <= CleanupRetryDelays.Length; attempt++)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(decision.LocalRootPath))
                    {
                        string? inspectionFailure = InspectForRemoval(null, decision.LocalRootPath);
                        if (inspectionFailure is not null)
                        {
                            return new WindowsVirtualFilesRootCleanupResult(
                                false,
                                "Local root was preserved because it changed before cleanup: " + inspectionFailure);
                        }

                        Directory.Delete(decision.LocalRootPath, recursive: true);
                    }

                    return new WindowsVirtualFilesRootCleanupResult(
                        true,
                        "Local root was removed after Cloud Files unregister.");
                }
                catch (Exception exception) when (
                    IsExpectedFileSystemFailure(exception)
                    && attempt < CleanupRetryDelays.Length)
                {
                    await Task.Delay(CleanupRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
                {
                    return new WindowsVirtualFilesRootCleanupResult(
                        false,
                        "Local root was preserved because cleanup failed: " + exception.Message);
                }
            }

            return new WindowsVirtualFilesRootCleanupResult(
                false,
                "Local root was preserved because cleanup did not complete.");
        }

        private static WindowsVirtualFilesRootCleanupDecision Skip(string localRootPath, string reason)
        {
            return new WindowsVirtualFilesRootCleanupDecision(
                localRootPath,
                ShouldRemoveRoot: false,
                reason);
        }

        internal static bool IsSafeCloudFilesPlaceholder(FileAttributes attributes)
        {
            return HasRawAttribute(attributes, FileAttributeUnpinned)
                && (HasRawAttribute(attributes, FileAttributeRecallOnOpen)
                    || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private string? InspectForRemoval(SyncPairSettings? syncPair, string rootPath)
        {
            try
            {
                var pending = new Stack<string>();
                pending.Push(rootPath);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    foreach (string filePath in Directory.EnumerateFiles(current, "*", ChildEnumeration))
                    {
                        FileAttributes attributes = File.GetAttributes(filePath);
                        if (!IsSafeCloudFilesPlaceholder(attributes)
                            && !IsKnownCloudFilesPlaceholder(syncPair, rootPath, filePath))
                        {
                            return "Local root contains at least one regular local file.";
                        }
                    }

                    foreach (string directoryPath in Directory.EnumerateDirectories(current, "*", ChildEnumeration))
                    {
                        FileAttributes attributes = File.GetAttributes(directoryPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0
                            && !IsSafeCloudFilesPlaceholder(attributes)
                            && !IsKnownCloudFilesPlaceholder(syncPair, rootPath, directoryPath))
                        {
                            return "Local root contains at least one reparse directory.";
                        }

                        pending.Push(directoryPath);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
            {
                return "Local root could not be inspected safely: " + exception.Message;
            }

            return null;
        }

        private bool IsKnownCloudFilesPlaceholder(SyncPairSettings? syncPair, string rootPath, string fullPath)
        {
            if (syncPair is null)
            {
                return false;
            }

            string relativePath = Path.GetRelativePath(rootPath, fullPath);
            if (string.IsNullOrWhiteSpace(relativePath)
                || string.Equals(relativePath, ".", StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath)
                || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (_knownCloudFilesRelativePathKeys.Contains(SyncPath.ToKey(normalizedPath)))
                {
                    return true;
                }

                if (_cloudFiles is null)
                {
                    return false;
                }

                WindowsCloudFilesPlaceholderState state = _cloudFiles.GetPlaceholderState(syncPair, normalizedPath);
                return state.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder);
            }
            catch (Exception exception) when (IsExpectedCloudFilesInspectionFailure(exception))
            {
                return false;
            }
        }

        private static bool HasRawAttribute(FileAttributes attributes, int rawAttribute)
        {
            return (((int)attributes) & rawAttribute) == rawAttribute;
        }

        private static bool IsExpectedFileSystemFailure(Exception exception)
        {
            return exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException
                or PathTooLongException
                or NotSupportedException;
        }

        private static bool IsExpectedCloudFilesInspectionFailure(Exception exception)
        {
            return IsExpectedFileSystemFailure(exception)
                || exception is ArgumentException
                or WindowsCloudFilesNativeException;
        }
    }
}
