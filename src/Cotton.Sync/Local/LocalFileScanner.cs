// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.State;

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Scans a local folder and hashes files for synchronization.
    /// </summary>
    public class LocalFileScanner :
        ILocalFileScanner,
        ILocalTreeScanner,
        ILocalFileMetadataTreeScanner,
        ILocalFileMetadataTreeProgressScanner,
        ILocalFileMetadataTreeLookupScanner,
        ILocalFileMetadataPathLookupScanner,
        ILocalFilePresenceProbe,
        ILocalFileContentHashProgressHasher
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            LocalTreeSnapshot tree = await ScanTreeAsync(rootPath, cancellationToken).ConfigureAwait(false);
            return tree.Files;
        }

        /// <inheritdoc />
        public async Task<LocalTreeSnapshot> ScanTreeAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            LocalTreeSnapshot tree = new LocalTreeSnapshot();
            await LocalTreeTraversal.ScanAsync(
                    rootPath,
                    computeHashes: true,
                    progress: null,
                    tree.Directories.Add,
                    tree.Files.Add,
                    cancellationToken)
                .ConfigureAwait(false);
            LocalTreeTraversal.Sort(tree);
            return tree;
        }

        /// <inheritdoc />
        public async Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            return await ScanTreeMetadataAsync(rootPath, progress: null, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
            string rootPath,
            IProgress<LocalTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            LocalTreeSnapshot tree = new LocalTreeSnapshot();
            await LocalTreeTraversal.ScanAsync(
                    rootPath,
                    computeHashes: false,
                    progress,
                    tree.Directories.Add,
                    tree.Files.Add,
                    cancellationToken)
                .ConfigureAwait(false);
            LocalTreeTraversal.Sort(tree);
            return tree;
        }

        /// <inheritdoc />
        public async Task<LocalTreeLookupSnapshot> ScanTreeMetadataLookupsAsync(
            string rootPath,
            IProgress<LocalTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            LocalTreeLookupSnapshot tree = new LocalTreeLookupSnapshot();
            await LocalTreeTraversal.ScanAsync(
                    rootPath,
                    computeHashes: false,
                    progress,
                    directory => SyncPathLookup.Add(tree.DirectoriesByPath, directory, static item => item.RelativePath),
                    file => SyncPathLookup.Add(tree.FilesByPath, file, static item => item.RelativePath),
                    cancellationToken)
                .ConfigureAwait(false);
            return tree;
        }

        /// <inheritdoc />
        public async Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
            string rootPath,
            IReadOnlyCollection<string> relativePaths,
            IProgress<LocalTreeScanProgress>? progress,
            bool includeDirectoryDescendants,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentNullException.ThrowIfNull(relativePaths);
            string fullRoot = Path.GetFullPath(rootPath);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException($"Local sync root was not found: {fullRoot}");
            }

            LocalTreeLookupSnapshot tree = new LocalTreeLookupSnapshot();
            HashSet<string> targetKeys = new HashSet<string>(
                relativePaths.Select(path => SyncPath.ToKey(SyncPath.Normalize(path))),
                StringComparer.OrdinalIgnoreCase);
            int filesScanned = 0;
            int directoriesScanned = 0;
            progress?.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            foreach (string relativePath in ExpandAncestors(relativePaths))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(relativePath) || LocalFileIgnoreRules.ShouldIgnore(relativePath))
                {
                    continue;
                }

                string normalizedPath = SyncPath.Normalize(relativePath);
                string fullPath = GetScopedFullPath(fullRoot, normalizedPath);
                if (!TryReadScopedPathAttributes(fullPath, out FileAttributes attributes))
                {
                    continue;
                }

                (int AddedFiles, int AddedDirectories) result = (attributes & FileAttributes.Directory) == 0
                    ? await ScanScopedFileAsync(
                            tree,
                            fullPath,
                            normalizedPath,
                            attributes,
                            progress,
                            filesScanned,
                            directoriesScanned,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await ScanScopedDirectoryAsync(
                            tree,
                            fullRoot,
                            fullPath,
                            normalizedPath,
                            attributes,
                            targetKeys,
                            includeDirectoryDescendants,
                            progress,
                            filesScanned,
                            directoriesScanned,
                            cancellationToken)
                        .ConfigureAwait(false);
                filesScanned += result.AddedFiles;
                directoriesScanned += result.AddedDirectories;
            }

            progress?.Report(new LocalTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            return tree;
        }

        /// <inheritdoc />
        public bool FileExists(string rootPath, string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            string normalizedPath = SyncPath.Normalize(relativePath);
            string fullPath = GetScopedFullPath(fullRoot, normalizedPath);
            return File.Exists(fullPath);
        }

        private static async Task<(int AddedFiles, int AddedDirectories)> ScanScopedFileAsync(
            LocalTreeLookupSnapshot tree,
            string fullPath,
            string normalizedPath,
            FileAttributes attributes,
            IProgress<LocalTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            CancellationToken cancellationToken)
        {
            FileInfo file = new(fullPath);
            bool isCloudFilesPlaceholder = LocalFilePlatformProbe.IsCloudFilesPlaceholder(file, attributes);
            if ((attributes & FileAttributes.ReparsePoint) != 0 && !isCloudFilesPlaceholder)
            {
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    file.FullName,
                    "the scoped path is an unsupported file reparse point.");
            }

            bool isOnlineOnly = isCloudFilesPlaceholder
                && LocalFilePlatformProbe.IsCloudFilesOnlineOnlyAttributes(attributes);
            LocalFileSnapshot snapshot = await LocalFileSnapshotFactory.CreateAsync(
                    file,
                    normalizedPath,
                    computeHash: false,
                    isCloudFilesPlaceholder,
                    isOnlineOnly,
                    cancellationToken)
                .ConfigureAwait(false);
            AddFile(tree, snapshot);
            LocalTreeTraversal.ReportFileProgress(progress, filesScanned + 1, directoriesScanned, normalizedPath);
            return (1, 0);
        }

        private static async Task<(int AddedFiles, int AddedDirectories)> ScanScopedDirectoryAsync(
            LocalTreeLookupSnapshot tree,
            string fullRoot,
            string fullPath,
            string normalizedPath,
            FileAttributes attributes,
            IReadOnlySet<string> targetKeys,
            bool includeDirectoryDescendants,
            IProgress<LocalTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            CancellationToken cancellationToken)
        {
            DirectoryInfo directory = new(fullPath);
            bool isCloudFilesPlaceholder = LocalFilePlatformProbe.IsCloudFilesPlaceholder(directory, attributes);
            if (!ShouldIncludeScopedDirectory(attributes, isCloudFilesPlaceholder))
            {
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    directory.FullName,
                    "the scoped path contains an unsupported directory reparse point.");
            }

            AddDirectory(tree, new LocalDirectorySnapshot
            {
                RelativePath = normalizedPath,
                FullPath = directory.FullName,
            });
            LocalTreeTraversal.ReportDirectoryProgress(
                progress,
                filesScanned,
                directoriesScanned + 1,
                normalizedPath);
            if (isCloudFilesPlaceholder
                || !includeDirectoryDescendants
                || !targetKeys.Contains(SyncPath.ToKey(normalizedPath)))
            {
                return (0, 1);
            }

            int descendantFiles = 0;
            int descendantDirectories = 0;
            await LocalTreeTraversal.ScanAsync(
                    fullRoot,
                    directory.FullName,
                    computeHashes: false,
                    progress,
                    descendant =>
                    {
                        AddDirectory(tree, descendant);
                        descendantDirectories++;
                    },
                    descendant =>
                    {
                        AddFile(tree, descendant);
                        descendantFiles++;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return (descendantFiles, descendantDirectories + 1);
        }

        private static bool TryReadScopedPathAttributes(string fullPath, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(fullPath);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<string> ComputeContentHashAsync(
            LocalFileSnapshot localFile,
            CancellationToken cancellationToken = default)
        {
            return await ComputeContentHashAsync(localFile, progress: null, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string> ComputeContentHashAsync(
            LocalFileSnapshot localFile,
            IProgress<SyncTransferProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(localFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(localFile.FullPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(localFile.RelativePath);
            return await LocalFileContentHasher.ComputeAsync(
                    localFile.FullPath,
                    localFile.RelativePath,
                    progress,
                    localFile.SizeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static IEnumerable<string> ExpandAncestors(IEnumerable<string> relativePaths)
        {
            HashSet<string> yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string current = string.Empty;
                for (int index = 0; index < segments.Length; index++)
                {
                    current = string.IsNullOrEmpty(current) ? segments[index] : current + "/" + segments[index];
                    if (yielded.Add(current))
                    {
                        yield return current;
                    }
                }
            }
        }

        private static string GetScopedFullPath(string fullRoot, string relativePath)
        {
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            LocalTreeTraversal.EnsurePathUnderRoot(fullRoot, fullPath);
            return fullPath;
        }

        private static void AddDirectory(LocalTreeLookupSnapshot tree, LocalDirectorySnapshot directory)
        {
            string key = SyncPath.ToKey(directory.RelativePath);
            tree.DirectoriesByPath.TryAdd(key, directory);
        }

        private static void AddFile(LocalTreeLookupSnapshot tree, LocalFileSnapshot file)
        {
            string key = SyncPath.ToKey(file.RelativePath);
            tree.FilesByPath.TryAdd(key, file);
        }

        internal static bool ShouldIncludeScopedDirectory(
            FileAttributes attributes,
            bool isCloudFilesPlaceholder)
        {
            return LocalFilePlatformProbe.ShouldIncludeScopedDirectory(attributes, isCloudFilesPlaceholder);
        }

        internal static bool IsCloudFilesReparseTag(uint reparseTag)
        {
            return LocalFilePlatformProbe.IsCloudFilesReparseTag(reparseTag);
        }

        internal static bool IsCloudFilesOnlineOnlyAttributes(FileAttributes attributes)
        {
            return LocalFilePlatformProbe.IsCloudFilesOnlineOnlyAttributes(attributes);
        }

        internal static string CreateReparseTagOpenPath(string fullPath)
        {
            return LocalFilePlatformProbe.CreateReparseTagOpenPath(fullPath);
        }

    }
}
