// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal partial class RemoteDirectoryDuplicateCoalescer(
        ILocalFileMetadataPathLookupScanner? localPathScanner,
        IRemotePathLookupCrawler? remotePathCrawler,
        ISyncStateStore stateStore,
        SyncLocalContentHashResolver contentHashResolver,
        ILogger logger,
        IRemoteDirectorySynchronizer? remoteDirectories)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task CoalesceAsync(SyncRunContext context)
        {
            if (localPathScanner is null || remotePathCrawler is null)
            {
                return;
            }

            await CoalesceUntrackedNameCollisionsAsync(context).ConfigureAwait(false);

            Dictionary<Guid, RemoteDirectorySnapshot> remoteById =
                RemoteDirectoryMovePlanner.BuildUniqueRemoteDirectoriesById(context.RemoteDirectoriesByPath.Values);
            foreach (SyncStateEntry source in context.DirectoryStateByPath.Values
                         .OrderBy(entry => GetPathDepth(entry.RelativePath)).ToArray())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                string sourceKey = SyncPath.ToKey(source.RelativePath);
                if (!context.DirectoryStateByPath.ContainsKey(sourceKey)
                    || !source.RemoteNodeId.HasValue
                    || !remoteById.TryGetValue(source.RemoteNodeId.Value, out RemoteDirectorySnapshot? target)
                    || PathComparer.Equals(sourceKey, SyncPath.ToKey(target.RelativePath))
                    || !Directory.Exists(GetFullPath(context.SyncPair.LocalRootPath, source.RelativePath))
                    || !Directory.Exists(GetFullPath(context.SyncPair.LocalRootPath, target.RelativePath)))
                {
                    continue;
                }

                if (await TryCoalesceAsync(context, source.RelativePath, target.RelativePath, remoteUsesSourcePath: false)
                        .ConfigureAwait(false))
                {
                    logger.LogInformation(
                        "Coalesced identical local directory trees at {SourcePath} and {TargetPath}.",
                        source.RelativePath,
                        target.RelativePath);
                }
            }
        }

        private async Task<bool> TryCoalesceAsync(
            SyncRunContext context,
            string sourcePath,
            string targetPath,
            bool remoteUsesSourcePath)
        {
            string sourceKey = SyncPath.ToKey(sourcePath);
            string targetKey = SyncPath.ToKey(targetPath);
            if (IsSameOrDescendantPathKey(targetKey, sourceKey)
                || IsSameOrDescendantPathKey(sourceKey, targetKey)
                || !AreEquivalentSiblingNames(sourcePath, targetPath)
                || await stateStore.GetAsync(context.SyncPair.SyncPairId, targetPath, context.CancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                return false;
            }

            if (remoteUsesSourcePath)
            {
                await foreach (SyncStateEntry _ in stateStore.LoadEntriesByPathPrefixAsync(
                                   context.SyncPair.SyncPairId, targetPath, context.CancellationToken)
                                   .ConfigureAwait(false))
                {
                    return false;
                }
            }

            List<SyncStateEntry> states = [];
            await foreach (SyncStateEntry state in stateStore.LoadEntriesByPathPrefixAsync(
                               context.SyncPair.SyncPairId,
                               sourcePath,
                               context.CancellationToken).ConfigureAwait(false))
            {
                states.Add(state);
            }

            if (states.Count == 0)
            {
                return false;
            }

            LocalTreeLookupSnapshot localSource = FilterLocal(await ScanLocalAsync(
                context,
                states.Select(state => state.RelativePath).ToArray(),
                includeDescendants: false).ConfigureAwait(false), sourceKey);
            LocalTreeLookupSnapshot localTarget = FilterLocal(await ScanLocalAsync(
                context,
                [targetPath],
                includeDescendants: true).ConfigureAwait(false), targetKey);
            RemoteTreeLookupSnapshot remoteTarget = await ScanRemoteAsync(
                context, remoteUsesSourcePath ? sourcePath : targetPath).ConfigureAwait(false);
            if (remoteUsesSourcePath)
            {
                remoteTarget = RebaseRemote(remoteTarget, sourcePath, targetPath);
            }
            if (!DirectoryShapeMatches(
                    context.SyncPair.LocalRootPath,
                    sourcePath,
                    targetPath,
                    states,
                    localSource,
                    localTarget,
                    remoteTarget))
            {
                return false;
            }

            foreach (SyncStateEntry state in states.Where(entry => entry.Kind == SyncEntryKind.File))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                string sourceFileKey = SyncPath.ToKey(state.RelativePath);
                string targetFilePath = ReplacePathPrefix(state.RelativePath, sourcePath, targetPath);
                string targetFileKey = SyncPath.ToKey(targetFilePath);
                LocalFileSnapshot sourceFile = localSource.FilesByPath[sourceFileKey];
                LocalFileSnapshot targetFile = localTarget.FilesByPath[targetFileKey];
                RemoteFileSnapshot remoteFile = remoteTarget.FilesByPath[targetFileKey];
                if (!RemoteMatchesBaseline(remoteFile.File, state)
                    || state.RemoteFileId != remoteFile.File.Id
                    || remoteFile.File.SizeBytes != targetFile.SizeBytes
                    || sourceFile.SizeBytes != targetFile.SizeBytes)
                {
                    return false;
                }

                if (sourceFile.IsCloudFilesOnlineOnlyPlaceholder)
                {
                    await contentHashResolver.EnsureForBaselineComparisonAsync(
                        sourceFile, state, context.Options, context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await contentHashResolver.EnsureAsync(
                        sourceFile, context.Options, context.CancellationToken).ConfigureAwait(false);
                }
                await contentHashResolver.EnsureAsync(targetFile, context.Options, context.CancellationToken)
                    .ConfigureAwait(false);
                if (!ContentMatches(sourceFile.ContentHash, remoteFile.File.ContentHash)
                    || !ContentMatches(targetFile.ContentHash, remoteFile.File.ContentHash))
                {
                    return false;
                }
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            if (remoteUsesSourcePath)
            {
                RemoveSubtree(context.LocalDirectoriesByPath, targetKey);
                RemoveSubtree(context.LocalFilesByPath, targetKey);
                SyncActivityReporter.Record(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Converged,
                    targetPath,
                    "Kept an identical local folder without creating a duplicate cloud folder.");
                return true;
            }

            await stateStore.DeleteByPathPrefixAsync(context.SyncPair.SyncPairId, sourcePath, context.CancellationToken)
                .ConfigureAwait(false);

            List<SyncStateEntry> targetStates = [];
            foreach (SyncStateEntry state in states)
            {
                string path = ReplacePathPrefix(state.RelativePath, sourcePath, targetPath);
                string key = SyncPath.ToKey(path);
                switch (state.Kind)
                {
                    case SyncEntryKind.Directory:
                        targetStates.Add(BuildDirectoryBaseline(
                            context.SyncPair, path, remoteTarget.DirectoriesByPath[key].Node));
                        break;
                    case SyncEntryKind.File:
                        LocalFileSnapshot local = localTarget.FilesByPath[key];
                        targetStates.Add(BuildBaseline(
                            context.SyncPair,
                            path,
                            local.ContentHash,
                            local.LastWriteUtc,
                            local.SizeBytes,
                            remoteTarget.FilesByPath[key].File));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(state), state.Kind, "Unknown sync entry kind.");
                }
            }

            await stateStore.UpsertManyAsync(targetStates, context.CancellationToken).ConfigureAwait(false);
            RemoveSubtree(context.LocalDirectoriesByPath, sourceKey);
            RemoveSubtree(context.LocalFilesByPath, sourceKey);
            RemoveSubtree(context.DirectoryStateByPath, sourceKey);
            RemoveSubtree(context.FileStateByPath, sourceKey);
            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in localTarget.DirectoriesByPath)
            {
                context.LocalDirectoriesByPath[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> entry in localTarget.FilesByPath)
            {
                context.LocalFilesByPath[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, RemoteDirectorySnapshot> entry in remoteTarget.DirectoriesByPath)
            {
                context.RemoteDirectoriesByPath[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, RemoteFileSnapshot> entry in remoteTarget.FilesByPath)
            {
                context.RemoteFilesByPath[entry.Key] = entry.Value;
            }

            foreach (SyncStateEntry state in targetStates)
            {
                string key = SyncPath.ToKey(state.RelativePath);
                switch (state.Kind)
                {
                    case SyncEntryKind.Directory:
                        context.DirectoryStateByPath[key] = state;
                        break;
                    case SyncEntryKind.File:
                        context.FileStateByPath[key] = state;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(state), state.Kind, "Unknown sync entry kind.");
                }
            }

            SyncActivityReporter.Record(
                context.Result,
                context.Options,
                SyncActivityKind.Converged,
                targetPath,
                "Matched identical local folders to the existing cloud folder.");
            return true;
        }

        private async Task<LocalTreeLookupSnapshot> ScanLocalAsync(
            SyncRunContext context,
            IReadOnlyCollection<string> paths,
            bool includeDescendants)
        {
            return await localPathScanner!.ScanPathMetadataLookupsAsync(
                context.SyncPair.LocalRootPath,
                paths,
                progress: null,
                includeDescendants,
                context.CancellationToken).ConfigureAwait(false);
        }

        private async Task<RemoteTreeLookupSnapshot> ScanRemoteAsync(SyncRunContext context, string targetPath)
        {
            RemoteTreeLookupSnapshot input = await remotePathCrawler!.CrawlPathLookupsAsync(
                context.SyncPair.RemoteRootNodeId,
                [targetPath],
                progress: null,
                context.CancellationToken).ConfigureAwait(false);
            string rootKey = SyncPath.ToKey(targetPath);
            RemoteTreeLookupSnapshot snapshot = new() { RootNode = input.RootNode };
            foreach (KeyValuePair<string, RemoteDirectorySnapshot> entry in input.DirectoriesByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, rootKey)))
            {
                snapshot.DirectoriesByPath.Add(entry.Key, entry.Value);
            }

            foreach (KeyValuePair<string, RemoteFileSnapshot> entry in input.FilesByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, rootKey)))
            {
                snapshot.FilesByPath.Add(entry.Key, entry.Value);
            }

            return snapshot;
        }

        private static LocalTreeLookupSnapshot FilterLocal(LocalTreeLookupSnapshot input, string rootKey)
        {
            LocalTreeLookupSnapshot snapshot = new();
            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in input.DirectoriesByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, rootKey)))
            {
                snapshot.DirectoriesByPath[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> entry in input.FilesByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, rootKey)))
            {
                snapshot.FilesByPath[entry.Key] = entry.Value;
            }

            return snapshot;
        }

        private static bool DirectoryShapeMatches(
            string localRootPath,
            string sourcePath,
            string targetPath,
            IReadOnlyCollection<SyncStateEntry> states,
            LocalTreeLookupSnapshot localSource,
            LocalTreeLookupSnapshot localTarget,
            RemoteTreeLookupSnapshot remoteTarget)
        {
            HashSet<string> sourceDirectories = states.Where(entry => entry.Kind == SyncEntryKind.Directory)
                .Select(entry => SyncPath.ToKey(entry.RelativePath)).ToHashSet(PathComparer);
            HashSet<string> sourceFiles = states.Where(entry => entry.Kind == SyncEntryKind.File)
                .Select(entry => SyncPath.ToKey(entry.RelativePath)).ToHashSet(PathComparer);
            HashSet<string> targetDirectories = sourceDirectories
                .Select(key => SyncPath.ToKey(ReplacePathPrefix(key, sourcePath, targetPath)))
                .ToHashSet(PathComparer);
            HashSet<string> targetFiles = sourceFiles
                .Select(key => SyncPath.ToKey(ReplacePathPrefix(key, sourcePath, targetPath)))
                .ToHashSet(PathComparer);
            if (!sourceDirectories.SetEquals(localSource.DirectoriesByPath.Keys)
                || !sourceFiles.SetEquals(localSource.FilesByPath.Keys)
                || !targetDirectories.SetEquals(localTarget.DirectoriesByPath.Keys)
                || !targetFiles.SetEquals(localTarget.FilesByPath.Keys)
                || !targetDirectories.SetEquals(remoteTarget.DirectoriesByPath.Keys)
                || !targetFiles.SetEquals(remoteTarget.FilesByPath.Keys))
            {
                return false;
            }

            foreach (SyncStateEntry state in states)
            {
                string key = SyncPath.ToKey(ReplacePathPrefix(state.RelativePath, sourcePath, targetPath));
                if (state.Kind == SyncEntryKind.Directory
                    && state.RemoteNodeId != remoteTarget.DirectoriesByPath[key].Node.Id)
                {
                    return false;
                }
            }

            foreach (string directoryKey in sourceDirectories)
            {
                string directoryPath = localSource.DirectoriesByPath[directoryKey].FullPath;
                HashSet<string> expectedNames = states
                    .Where(entry => PathComparer.Equals(
                        SyncPath.ToKey(Path.GetDirectoryName(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                            ?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty),
                        directoryKey))
                    .Select(entry => Path.GetFileName(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .ToHashSet(PathComparer);
                HashSet<string> actualNames = Directory.EnumerateFileSystemEntries(directoryPath)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToHashSet(PathComparer);
                if (!expectedNames.SetEquals(actualNames))
                {
                    return false;
                }
            }

            return Directory.Exists(GetFullPath(localRootPath, sourcePath));
        }

        private static string GetFullPath(string rootPath, string relativePath)
        {
            return Path.Combine(rootPath, SyncPath.Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool AreEquivalentSiblingNames(string sourcePath, string targetPath)
        {
            string normalizedSource = SyncPath.Normalize(sourcePath);
            string normalizedTarget = SyncPath.Normalize(targetPath);
            int sourceSeparator = normalizedSource.LastIndexOf('/');
            int targetSeparator = normalizedTarget.LastIndexOf('/');
            if (sourceSeparator != targetSeparator
                || !string.Equals(
                    normalizedSource[..(sourceSeparator + 1)],
                    normalizedTarget[..(targetSeparator + 1)],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(
                RemoteNameKey.Create(normalizedSource[(sourceSeparator + 1)..]),
                RemoteNameKey.Create(normalizedTarget[(targetSeparator + 1)..]),
                StringComparison.Ordinal);
        }

        private static void RemoveSubtree<T>(IDictionary<string, T> entries, string sourceKey)
        {
            foreach (string key in entries.Keys.Where(key => IsSameOrDescendantPathKey(key, sourceKey)).ToArray())
            {
                entries.Remove(key);
            }
        }
    }
}
