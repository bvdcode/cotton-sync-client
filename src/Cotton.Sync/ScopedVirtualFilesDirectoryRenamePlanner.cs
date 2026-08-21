// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class ScopedVirtualFilesDirectoryRenamePlanner(
        ILocalFileMetadataPathLookupScanner? localPathScanner,
        IRemotePathLookupCrawler? remotePathCrawler,
        ISyncStateStore stateStore)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        private bool CanExpandScopedVirtualFilesDirectoryRename(SyncPair syncPair, SyncRunOptions options)
        {
            return !options.Scope.IsFull
                && syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && localPathScanner is not null
                && remotePathCrawler is not null;
        }

        private static ScopedVirtualFilesDirectoryRenameCandidate? TryCreateScopedDirectoryRenameCandidate(
            IReadOnlySet<string> scopedKeys,
            SyncTreeLookups treeLookups,
            IDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            List<KeyValuePair<string, SyncStateEntry>> sourceDirectories = directoryStateByPath
                .Where(state =>
                    scopedKeys.Contains(state.Key)
                    && !treeLookups.LocalDirectoriesByPath.ContainsKey(state.Key)
                    && treeLookups.RemoteDirectoriesByPath.TryGetValue(state.Key, out RemoteDirectorySnapshot? remote)
                    && state.Value.RemoteNodeId == remote.Node.Id)
                .ToList();
            List<KeyValuePair<string, LocalDirectorySnapshot>> targetDirectories = treeLookups.LocalDirectoriesByPath
                .Where(local =>
                    scopedKeys.Contains(local.Key)
                    && !treeLookups.RemoteDirectoriesByPath.ContainsKey(local.Key)
                    && !directoryStateByPath.ContainsKey(local.Key))
                .ToList();
            List<KeyValuePair<string, SyncStateEntry>> sourceRootCandidates = sourceDirectories
                .Where(candidate => sourceDirectories.All(item => IsSameOrDescendantPathKey(item.Key, candidate.Key)))
                .ToList();
            List<KeyValuePair<string, LocalDirectorySnapshot>> targetRootCandidates = targetDirectories
                .Where(candidate => targetDirectories.All(item => IsSameOrDescendantPathKey(item.Key, candidate.Key)))
                .ToList();
            if (sourceRootCandidates.Count != 1 || targetRootCandidates.Count != 1)
            {
                return null;
            }

            KeyValuePair<string, SyncStateEntry> source = sourceRootCandidates[0];
            KeyValuePair<string, LocalDirectorySnapshot> target = targetRootCandidates[0];
            bool hasUnrelatedScopedPath = scopedKeys.Any(key =>
                !IsSameOrDescendantPathKey(key, source.Key)
                && !IsSameOrDescendantPathKey(source.Key, key)
                && !IsSameOrDescendantPathKey(key, target.Key)
                && !IsSameOrDescendantPathKey(target.Key, key));
            if (hasUnrelatedScopedPath)
            {
                return null;
            }

            return new ScopedVirtualFilesDirectoryRenameCandidate(
                source.Key,
                SyncPath.Normalize(source.Value.RelativePath),
                target.Key,
                SyncPath.Normalize(target.Value.RelativePath));
        }

        private async Task<List<SyncStateEntry>> LoadScopedRenameDescendantStatesAsync(
            string syncPairId,
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntry> descendantStates = [];
            await foreach (SyncStateEntry state in stateStore
                               .LoadEntriesByPathPrefixAsync(syncPairId, candidate.SourcePath, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        SyncPath.ToKey(state.RelativePath),
                        candidate.SourceKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    descendantStates.Add(state);
                }
            }

            return descendantStates;
        }

        private async Task<bool> HasStateAtPathPrefixAsync(
            string syncPairId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            await foreach (SyncStateEntry _ in stateStore
                               .LoadEntriesByPathPrefixAsync(syncPairId, relativePath, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }

        public async Task<ScopedVirtualFilesDirectoryRenamePlan?> ExpandAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncTreeLookups treeLookups,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (!CanExpandScopedVirtualFilesDirectoryRename(syncPair, options))
            {
                return null;
            }

            HashSet<string> scopedKeys = options.Scope.LocalChangedPaths
                .Select(SyncPath.ToKey)
                .ToHashSet(PathComparer);
            ScopedVirtualFilesDirectoryRenameCandidate? candidate = TryCreateScopedDirectoryRenameCandidate(
                scopedKeys,
                treeLookups,
                directoryStateByPath);
            if (candidate is null)
            {
                return null;
            }

            List<SyncStateEntry> descendantStates = await LoadScopedRenameDescendantStatesAsync(
                    syncPair.SyncPairId,
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
            if (descendantStates.Count == 0
                || await HasStateAtPathPrefixAsync(syncPair.SyncPairId, candidate.TargetPath, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }

            ScopedVirtualFilesDirectoryRenameValidation validation = await ScanScopedDirectoryRenameValidationAsync(
                    syncPair,
                    candidate,
                    descendantStates,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsScopedDirectoryRenameShapeConfirmed(candidate, validation)
                || !AreScopedDirectoryRenameStatesConfirmed(descendantStates, validation))
            {
                return null;
            }

            MergeScopedDirectoryRenameLookups(treeLookups, validation);
            MergeScopedDirectoryRenameState(directoryStateByPath, fileStateByPath, descendantStates);
            string[] sourceDirectoryKeys = [
                candidate.SourceKey,
                .. validation.ExpectedSourceDirectoryKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase),
            ];
            return new ScopedVirtualFilesDirectoryRenamePlan(
                candidate.SourcePath,
                sourceDirectoryKeys,
                validation.ExpectedSourceFileKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private async Task<ScopedVirtualFilesDirectoryRenameValidation> ScanScopedDirectoryRenameValidationAsync(
            SyncPair syncPair,
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            IReadOnlyList<SyncStateEntry> descendantStates,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> targetPathBySourceKey = descendantStates.ToDictionary(
                state => SyncPath.ToKey(state.RelativePath),
                state => candidate.TargetPath + SyncPath.Normalize(state.RelativePath)[candidate.SourcePath.Length..],
                PathComparer);
            LocalTreeLookupSnapshot localDescendants = await localPathScanner!
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    targetPathBySourceKey.Values.ToArray(),
                    progress: null,
                    includeDirectoryDescendants: false,
                    cancellationToken)
                .ConfigureAwait(false);
            RemoteTreeLookupSnapshot remoteDescendants = await remotePathCrawler!
                .CrawlPathLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    [candidate.SourcePath],
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            HashSet<string> expectedSourceDirectoryKeys = descendantStates
                .Where(static state => state.Kind == SyncEntryKind.Directory)
                .Select(state => SyncPath.ToKey(state.RelativePath))
                .ToHashSet(PathComparer);
            HashSet<string> expectedSourceFileKeys = descendantStates
                .Where(static state => state.Kind == SyncEntryKind.File)
                .Select(state => SyncPath.ToKey(state.RelativePath))
                .ToHashSet(PathComparer);
            return new ScopedVirtualFilesDirectoryRenameValidation(
                targetPathBySourceKey,
                expectedSourceDirectoryKeys,
                expectedSourceFileKeys,
                localDescendants,
                remoteDescendants);
        }

        private static bool IsScopedDirectoryRenameShapeConfirmed(
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            string sourcePrefix = candidate.SourceKey.TrimEnd('/') + "/";
            string targetPrefix = candidate.TargetKey.TrimEnd('/') + "/";
            HashSet<string> actualSourceDirectoryKeys = validation.RemoteDescendants.DirectoriesByPath.Keys
                .Where(key => key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualSourceFileKeys = validation.RemoteDescendants.FilesByPath.Keys
                .Where(key => key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualTargetDirectoryKeys = validation.LocalDescendants.DirectoriesByPath.Keys
                .Where(key => key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualTargetFileKeys = validation.LocalDescendants.FilesByPath.Keys
                .Where(key => key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> expectedTargetDirectoryKeys = validation.ExpectedSourceDirectoryKeys
                .Select(key => SyncPath.ToKey(validation.TargetPathBySourceKey[key]))
                .ToHashSet(PathComparer);
            HashSet<string> expectedTargetFileKeys = validation.ExpectedSourceFileKeys
                .Select(key => SyncPath.ToKey(validation.TargetPathBySourceKey[key]))
                .ToHashSet(PathComparer);
            return validation.RemoteDescendants.DirectoriesByPath.ContainsKey(candidate.SourceKey)
                && actualSourceDirectoryKeys.SetEquals(validation.ExpectedSourceDirectoryKeys)
                && actualSourceFileKeys.SetEquals(validation.ExpectedSourceFileKeys)
                && actualTargetDirectoryKeys.SetEquals(expectedTargetDirectoryKeys)
                && actualTargetFileKeys.SetEquals(expectedTargetFileKeys);
        }

        private static bool AreScopedDirectoryRenameStatesConfirmed(
            IReadOnlyList<SyncStateEntry> descendantStates,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            foreach (SyncStateEntry state in descendantStates)
            {
                string stateKey = SyncPath.ToKey(state.RelativePath);
                string targetKey = SyncPath.ToKey(validation.TargetPathBySourceKey[stateKey]);
                bool isConfirmed = state.Kind switch
                {
                    SyncEntryKind.Directory =>
                        validation.LocalDescendants.DirectoriesByPath.ContainsKey(targetKey)
                        && validation.RemoteDescendants.DirectoriesByPath.TryGetValue(
                            stateKey,
                            out RemoteDirectorySnapshot? remote)
                        && state.RemoteNodeId == remote.Node.Id,
                    SyncEntryKind.File =>
                        validation.LocalDescendants.FilesByPath.ContainsKey(targetKey)
                        && validation.RemoteDescendants.FilesByPath.TryGetValue(
                            stateKey,
                            out RemoteFileSnapshot? remote)
                        && RemoteMatchesBaseline(remote.File, state),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(state),
                        state.Kind,
                        "Unknown sync state entry kind."),
                };
                if (!isConfirmed)
                {
                    return false;
                }
            }

            return true;
        }

        private static void MergeScopedDirectoryRenameLookups(
            SyncTreeLookups treeLookups,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            foreach (KeyValuePair<string, LocalDirectorySnapshot> local in validation.LocalDescendants.DirectoriesByPath)
            {
                treeLookups.LocalDirectoriesByPath[local.Key] = local.Value;
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> local in validation.LocalDescendants.FilesByPath)
            {
                treeLookups.LocalFilesByPath[local.Key] = local.Value;
            }

            foreach (KeyValuePair<string, RemoteDirectorySnapshot> remote in validation.RemoteDescendants.DirectoriesByPath)
            {
                treeLookups.RemoteDirectoriesByPath[remote.Key] = remote.Value;
            }

            foreach (KeyValuePair<string, RemoteFileSnapshot> remote in validation.RemoteDescendants.FilesByPath)
            {
                treeLookups.RemoteFilesByPath[remote.Key] = remote.Value;
            }
        }

        private static void MergeScopedDirectoryRenameState(
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyList<SyncStateEntry> descendantStates)
        {
            foreach (SyncStateEntry state in descendantStates)
            {
                string key = SyncPath.ToKey(state.RelativePath);
                switch (state.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[key] = state;
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath[key] = state;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(state),
                            state.Kind,
                            "Unknown sync state entry kind.");
                }
            }
        }
    }
}
