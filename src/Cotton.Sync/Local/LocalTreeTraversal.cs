// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.State;

namespace Cotton.Sync.Local
{
    internal static class LocalTreeTraversal
    {
        private const int ProgressReportItemInterval = 100;
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly EnumerationOptions ChildEnumerationOptions = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        public static async Task ScanAsync(
            string rootPath,
            bool computeHashes,
            IProgress<LocalTreeScanProgress>? progress,
            Action<LocalDirectorySnapshot> addDirectory,
            Action<LocalFileSnapshot> addFile,
            CancellationToken cancellationToken)
        {
            await ScanAsync(
                    rootPath,
                    Path.GetFullPath(rootPath),
                    computeHashes,
                    progress,
                    addDirectory,
                    addFile,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task ScanAsync(
            string rootPath,
            string scanRootPath,
            bool computeHashes,
            IProgress<LocalTreeScanProgress>? progress,
            Action<LocalDirectorySnapshot> addDirectory,
            Action<LocalFileSnapshot> addFile,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(scanRootPath);
            ArgumentNullException.ThrowIfNull(addDirectory);
            ArgumentNullException.ThrowIfNull(addFile);
            string fullRoot = Path.GetFullPath(rootPath);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException($"Local sync root was not found: {fullRoot}");
            }

            string fullScanRoot = Path.GetFullPath(scanRootPath);
            EnsurePathUnderRoot(fullRoot, fullScanRoot);
            int directoriesScanned = 0;
            int filesScanned = 0;
            progress?.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            Stack<LocalDirectoryScanFrame> pendingDirectories = new();
            pendingDirectories.Push(CreateDirectoryScanFrame(fullRoot, fullScanRoot));
            try
            {
                while (pendingDirectories.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LocalDirectoryScanFrame currentDirectory = pendingDirectories.Peek();
                    if (TryReadNextChildFile(currentDirectory, fullRoot, out FileInfo? file, out string relativePath))
                    {
                        FileAttributes attributes = ReadFileAttributes(file, relativePath);
                        bool isCloudFilesPlaceholder = LocalFilePlatformProbe.IsCloudFilesPlaceholder(file, attributes);
                        bool isCloudFilesOnlineOnlyPlaceholder = isCloudFilesPlaceholder
                            && LocalFilePlatformProbe.IsCloudFilesOnlineOnlyAttributes(attributes);
                        LocalFileSnapshot fileSnapshot = await LocalFileSnapshotFactory.CreateAsync(
                                file,
                                relativePath,
                                computeHashes,
                                isCloudFilesPlaceholder,
                                isCloudFilesOnlineOnlyPlaceholder,
                                cancellationToken)
                            .ConfigureAwait(false);
                        addFile(fileSnapshot);
                        filesScanned++;
                        ReportFileProgress(progress, filesScanned, directoriesScanned, relativePath);
                        continue;
                    }

                    if (TryReadNextChildDirectory(currentDirectory, fullRoot, out LocalDirectorySnapshot? directory))
                    {
                        addDirectory(directory);
                        directoriesScanned++;
                        ReportDirectoryProgress(progress, filesScanned, directoriesScanned, directory.RelativePath);
                        pendingDirectories.Push(CreateDirectoryScanFrame(fullRoot, directory.FullPath));
                        continue;
                    }

                    pendingDirectories.Pop().Dispose();
                }
            }
            finally
            {
                while (pendingDirectories.Count > 0)
                {
                    pendingDirectories.Pop().Dispose();
                }
            }

            progress?.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
        }

        public static void Sort(LocalTreeSnapshot tree)
        {
            tree.Directories.Sort((left, right) => PathComparer.Compare(left.RelativePath, right.RelativePath));
            tree.Files.Sort((left, right) => PathComparer.Compare(left.RelativePath, right.RelativePath));
        }

        public static void ReportFileProgress(
            IProgress<LocalTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            string currentPath)
        {
            if (progress is null)
            {
                return;
            }

            if (filesScanned == 1 || filesScanned % ProgressReportItemInterval == 0)
            {
                progress.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath));
            }
        }

        public static void ReportDirectoryProgress(
            IProgress<LocalTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            string currentPath)
        {
            if (progress is null)
            {
                return;
            }

            if (directoriesScanned == 1 || directoriesScanned % ProgressReportItemInterval == 0)
            {
                progress.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath));
            }
        }

        public static void EnsurePathUnderRoot(string fullRoot, string fullPath)
        {
            string rootWithSeparator = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Scoped local path must stay under the sync root.", nameof(fullPath));
            }
        }

        private static bool TryReadNextChildDirectory(
            LocalDirectoryScanFrame currentDirectory,
            string fullRoot,
            out LocalDirectorySnapshot directory)
        {
            while (TryReadNextDirectoryPath(currentDirectory, fullRoot, out string? directoryPath))
            {
                string path = directoryPath ?? throw new InvalidOperationException("Directory enumeration returned a null path.");
                string relativePath = ToRelativePath(fullRoot, path);
                if (LocalFileIgnoreRules.ShouldIgnore(relativePath))
                {
                    continue;
                }

                DirectoryInfo directoryInfo = new(path);
                FileAttributes attributes = ReadDirectoryAttributes(fullRoot, directoryInfo, relativePath);
                bool isCloudFilesPlaceholder = LocalFilePlatformProbe.IsCloudFilesPlaceholder(directoryInfo, attributes);
                if (!LocalFilePlatformProbe.ShouldIncludeScopedDirectory(attributes, isCloudFilesPlaceholder))
                {
                    continue;
                }

                directory = new LocalDirectorySnapshot
                {
                    RelativePath = relativePath,
                    FullPath = directoryInfo.FullName,
                };
                return true;
            }

            directory = null!;
            return false;
        }

        private static bool TryReadNextChildFile(
            LocalDirectoryScanFrame currentDirectory,
            string fullRoot,
            out FileInfo file,
            out string relativePath)
        {
            while (TryReadNextFilePath(currentDirectory, fullRoot, out string? filePath))
            {
                string path = filePath ?? throw new InvalidOperationException("File enumeration returned a null path.");
                relativePath = ToRelativePath(fullRoot, path);
                if (LocalFileIgnoreRules.ShouldIgnore(relativePath))
                {
                    continue;
                }

                file = new FileInfo(path);
                FileAttributes attributes = ReadFileAttributes(file, relativePath);
                bool isCloudFilesPlaceholder = LocalFilePlatformProbe.IsCloudFilesPlaceholder(file, attributes);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !isCloudFilesPlaceholder)
                {
                    continue;
                }

                return true;
            }

            file = null!;
            relativePath = string.Empty;
            return false;
        }

        private static LocalDirectoryScanFrame CreateDirectoryScanFrame(string fullRoot, string directoryPath)
        {
            try
            {
                return new LocalDirectoryScanFrame(directoryPath, ChildEnumerationOptions);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw CreateDirectoryAccessException(fullRoot, directoryPath, exception);
            }
            catch (IOException exception)
            {
                throw CreateLocalPathUnavailableException(fullRoot, directoryPath, exception);
            }
        }

        private static bool TryReadNextDirectoryPath(
            LocalDirectoryScanFrame currentDirectory,
            string fullRoot,
            out string? directoryPath)
        {
            try
            {
                return currentDirectory.TryReadNextDirectoryPath(out directoryPath);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw CreateDirectoryAccessException(fullRoot, currentDirectory.DirectoryPath, exception);
            }
            catch (IOException exception)
            {
                throw CreateLocalPathUnavailableException(fullRoot, currentDirectory.DirectoryPath, exception);
            }
        }

        private static bool TryReadNextFilePath(
            LocalDirectoryScanFrame currentDirectory,
            string fullRoot,
            out string? filePath)
        {
            try
            {
                return currentDirectory.TryReadNextFilePath(out filePath);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw CreateDirectoryAccessException(fullRoot, currentDirectory.DirectoryPath, exception);
            }
            catch (IOException exception)
            {
                throw CreateLocalPathUnavailableException(fullRoot, currentDirectory.DirectoryPath, exception);
            }
        }

        private static FileAttributes ReadDirectoryAttributes(
            string fullRoot,
            DirectoryInfo directory,
            string relativePath)
        {
            try
            {
                return directory.Attributes;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw CreateDirectoryAccessException(fullRoot, directory.FullName, exception);
            }
            catch (IOException exception)
            {
                throw new LocalFileUnavailableException(relativePath, directory.FullName, exception);
            }
        }

        private static FileAttributes ReadFileAttributes(FileInfo file, string relativePath)
        {
            try
            {
                return file.Attributes;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalFilePermissionDeniedException(relativePath, file.FullName, exception);
            }
            catch (IOException exception)
            {
                throw new LocalFileUnavailableException(relativePath, file.FullName, exception);
            }
        }

        private static Exception CreateDirectoryAccessException(
            string fullRoot,
            string directoryPath,
            UnauthorizedAccessException exception)
        {
            string relativePath = ToRelativePathForException(fullRoot, directoryPath);
            if (!PathsEqual(fullRoot, directoryPath) || !DirectoryStillExists(directoryPath))
            {
                return new LocalFileUnavailableException(relativePath, directoryPath, exception);
            }

            return new LocalFilePermissionDeniedException(relativePath, directoryPath, exception);
        }

        private static LocalFileUnavailableException CreateLocalPathUnavailableException(
            string fullRoot,
            string fullPath,
            IOException exception)
        {
            return new LocalFileUnavailableException(ToRelativePathForException(fullRoot, fullPath), fullPath, exception);
        }

        private static string ToRelativePath(string rootPath, string filePath)
        {
            string relative = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            return SyncPath.Normalize(relative);
        }

        private static string ToRelativePathForException(string rootPath, string filePath)
        {
            string relative = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            return relative == "." ? "sync root" : SyncPath.Normalize(relative);
        }

        private static bool DirectoryStillExists(string directoryPath)
        {
            try
            {
                return Directory.Exists(directoryPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
