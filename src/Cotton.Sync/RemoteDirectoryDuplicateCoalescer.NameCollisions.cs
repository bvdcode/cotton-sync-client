// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal partial class RemoteDirectoryDuplicateCoalescer
    {
        private async Task CoalesceUntrackedNameCollisionsAsync(SyncRunContext context)
        {
            LocalDirectorySnapshot[] candidates = context.LocalDirectoriesByPath.Values
                .Where(directory => !context.DirectoryStateByPath.ContainsKey(SyncPath.ToKey(directory.RelativePath))
                    && !context.RemoteDirectoriesByPath.ContainsKey(SyncPath.ToKey(directory.RelativePath)))
                .OrderBy(directory => GetPathDepth(directory.RelativePath))
                .ToArray();
            if (candidates.Length == 0)
            {
                return;
            }

            HashSet<string> candidateParents = [];
            foreach (LocalDirectorySnapshot candidate in candidates)
            {
                string candidateParentPath = GetParentPath(candidate.RelativePath);
                candidateParents.Add(string.IsNullOrEmpty(candidateParentPath)
                    ? string.Empty
                    : SyncPath.ToKey(candidateParentPath));
            }

            Dictionary<(string ParentKey, string NameKey), RemoteDirectorySnapshot> remoteByName = new();
            foreach (RemoteDirectorySnapshot remote in context.RemoteDirectoriesByPath.Values)
            {
                string remoteParentPath = GetParentPath(remote.RelativePath);
                string remoteParentKey = string.IsNullOrEmpty(remoteParentPath)
                    ? string.Empty
                    : SyncPath.ToKey(remoteParentPath);
                if (candidateParents.Contains(remoteParentKey))
                {
                    remoteByName.TryAdd((remoteParentKey, RemoteNameKey.Create(remote.Node.Name)), remote);
                }
            }

            foreach (LocalDirectorySnapshot local in candidates)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                string targetPath = local.RelativePath;
                string targetKey = SyncPath.ToKey(targetPath);
                if (!context.LocalDirectoriesByPath.ContainsKey(targetKey)
                    || context.DirectoryStateByPath.ContainsKey(targetKey)
                    || context.RemoteDirectoriesByPath.ContainsKey(targetKey))
                {
                    continue;
                }

                string parentPath = GetParentPath(targetPath);
                string parentKey = string.IsNullOrEmpty(parentPath) ? string.Empty : SyncPath.ToKey(parentPath);
                Guid parentId = context.TreeLookups.RemoteRootNode.Id;
                if (!string.IsNullOrEmpty(parentKey))
                {
                    SyncStateEntry? parentState = await stateStore.GetAsync(
                        context.SyncPair.SyncPairId, parentPath, context.CancellationToken).ConfigureAwait(false);
                    parentId = context.RemoteDirectoriesByPath.TryGetValue(parentKey, out RemoteDirectorySnapshot? parent)
                        ? parent.Node.Id
                        : parentState?.RemoteNodeId ?? Guid.Empty;
                }
                string nameKey = RemoteNameKey.Create(GetFileName(targetPath));
                remoteByName.TryGetValue((parentKey, nameKey), out RemoteDirectorySnapshot? matched);
                NodeDto? remoteNode = matched?.Node;
                if (remoteNode is null
                    && parentId != Guid.Empty
                    && !context.Options.Scope.IsFull
                    && remoteDirectories is not null)
                {
                    remoteNode = await remoteDirectories.FindChildDirectoryAsync(
                        parentId, GetFileName(targetPath), context.CancellationToken).ConfigureAwait(false);
                }

                if (remoteNode is null || string.Equals(remoteNode.Name, GetFileName(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SyncStateEntry? sourceState = null;
                await foreach (SyncStateEntry state in stateStore.LoadEntriesByRemoteIdsAsync(
                                   context.SyncPair.SyncPairId,
                                   [remoteNode.Id],
                                   [],
                                   context.CancellationToken).ConfigureAwait(false))
                {
                    if (state.Kind == SyncEntryKind.Directory)
                    {
                        sourceState = state;
                        break;
                    }
                }

                if (sourceState is null)
                {
                    throw new InvalidOperationException(
                        $"Local folder '{targetPath}' has the same cloud name as '{remoteNode.Name}', "
                        + "but the existing cloud folder has no local sync baseline. The local folder was kept.");
                }

                if (!AreEquivalentSiblingNames(sourceState.RelativePath, targetPath)
                    || !Directory.Exists(GetFullPath(context.SyncPair.LocalRootPath, sourceState.RelativePath)))
                {
                    throw new InvalidOperationException(
                        $"Local folder '{targetPath}' has the same cloud name as '{sourceState.RelativePath}', "
                        + "but the original local folder could not be verified. The local folder was kept.");
                }

                if (!await TryCoalesceAsync(context, sourceState.RelativePath, targetPath, remoteUsesSourcePath: true)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Local folders '{sourceState.RelativePath}' and '{targetPath}' have equivalent cloud names, "
                        + "but their contents could not be verified as identical. Both folders were kept.");
                }

                logger.LogInformation(
                    "Kept identical local directory at {DuplicatePath}; cloud folder remains {SourcePath}.",
                    targetPath,
                    sourceState.RelativePath);
            }
        }

        private static RemoteTreeLookupSnapshot RebaseRemote(
            RemoteTreeLookupSnapshot source,
            string sourcePath,
            string targetPath)
        {
            RemoteTreeLookupSnapshot rebased = new() { RootNode = source.RootNode };
            foreach (RemoteDirectorySnapshot directory in source.DirectoriesByPath.Values)
            {
                string path = ReplacePathPrefix(directory.RelativePath, sourcePath, targetPath);
                rebased.DirectoriesByPath[SyncPath.ToKey(path)] = new RemoteDirectorySnapshot
                {
                    RelativePath = path,
                    Node = directory.Node,
                };
            }

            foreach (RemoteFileSnapshot file in source.FilesByPath.Values)
            {
                string path = ReplacePathPrefix(file.RelativePath, sourcePath, targetPath);
                rebased.FilesByPath[SyncPath.ToKey(path)] = new RemoteFileSnapshot
                {
                    RelativePath = path,
                    File = file.File,
                };
            }

            return rebased;
        }
    }
}
