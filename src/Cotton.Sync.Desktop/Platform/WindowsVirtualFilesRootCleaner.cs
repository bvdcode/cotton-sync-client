// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsVirtualFilesRootCleaner : IWindowsVirtualFilesRootCleaner
    {
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private static readonly EnumerationOptions ChildEnumeration = new()
        {
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
        };

        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;

        public WindowsVirtualFilesRootCleaner(WindowsVirtualFilesRootSafetyPolicy? rootSafety = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
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

            string? inspectionFailure = InspectForRemoval(rootPath);
            if (inspectionFailure is not null)
            {
                return Skip(rootPath, inspectionFailure);
            }

            return new WindowsVirtualFilesRootCleanupDecision(
                rootPath,
                ShouldRemoveRoot: true,
                "Local root contains only directories and Cloud Files placeholders.");
        }

        public Task<WindowsVirtualFilesRootCleanupResult> CleanupAfterUnregisterAsync(
            WindowsVirtualFilesRootCleanupDecision decision,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(decision);
            cancellationToken.ThrowIfCancellationRequested();
            if (!decision.ShouldRemoveRoot)
            {
                return Task.FromResult(new WindowsVirtualFilesRootCleanupResult(false, decision.Reason));
            }

            try
            {
                if (Directory.Exists(decision.LocalRootPath))
                {
                    string? inspectionFailure = InspectForRemoval(decision.LocalRootPath);
                    if (inspectionFailure is not null)
                    {
                        return Task.FromResult(new WindowsVirtualFilesRootCleanupResult(
                            false,
                            "Local root was preserved because it changed before cleanup: " + inspectionFailure));
                    }

                    Directory.Delete(decision.LocalRootPath, recursive: true);
                }

                return Task.FromResult(new WindowsVirtualFilesRootCleanupResult(
                    true,
                    "Local root was removed after Cloud Files unregister."));
            }
            catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
            {
                return Task.FromResult(new WindowsVirtualFilesRootCleanupResult(
                    false,
                    "Local root was preserved because cleanup failed: " + exception.Message));
            }
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

        private static string? InspectForRemoval(string rootPath)
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
                        if (!IsSafeCloudFilesPlaceholder(attributes))
                        {
                            return "Local root contains at least one regular local file.";
                        }
                    }

                    foreach (string directoryPath in Directory.EnumerateDirectories(current, "*", ChildEnumeration))
                    {
                        FileAttributes attributes = File.GetAttributes(directoryPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
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
    }
}
