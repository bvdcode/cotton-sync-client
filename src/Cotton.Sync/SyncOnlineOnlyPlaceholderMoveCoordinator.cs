// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.RemoteSyncErrorClassifier;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class SyncOnlineOnlyPlaceholderMoveCoordinator(
        SyncFileMaterializer fileMaterializer,
        ISyncStateStore stateStore,
        IRemoteFileSynchronizer remoteFiles,
        SyncRemoteFileTransfer fileTransfer)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task CoalesceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return;
            }

            OnlineOnlyPlaceholderMoveContext context = new OnlineOnlyPlaceholderMoveContext(
                syncPair,
                options,
                result,
                localByPath,
                remoteByPath,
                stateByPath,
                cancellationToken);
            IReadOnlySet<string> scopedKeys = BuildExactScopedPathKeys(options.Scope.LocalChangedPaths);
            IReadOnlySet<string> explicitlyDeletedKeys = BuildExactScopedPathKeys(options.Scope.LocalDeletedPaths);
            IReadOnlyList<OnlineOnlyPlaceholderMoveSource> sources = FindOnlineOnlyPlaceholderMoveSources(
                context,
                explicitlyDeletedKeys);
            if (sources.Count == 0)
            {
                return;
            }

            IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> targets = FindOnlineOnlyPlaceholderMoveTargets(context);
            IReadOnlyList<OnlineOnlyPlaceholderMoveMatch> matches = FindUnambiguousOnlineOnlyPlaceholderMoveMatches(
                options,
                scopedKeys,
                sources,
                targets);
            foreach (OnlineOnlyPlaceholderMoveMatch match in matches)
            {
                await ApplyOnlineOnlyPlaceholderMoveAsync(context, match).ConfigureAwait(false);
            }
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveSource> FindOnlineOnlyPlaceholderMoveSources(
            OnlineOnlyPlaceholderMoveContext context,
            IReadOnlySet<string> explicitlyDeletedKeys)
        {
            List<OnlineOnlyPlaceholderMoveSource> sources = [];
            foreach (KeyValuePair<string, SyncStateEntry> state in context.StateByPath)
            {
                if (!IsOnlineOnlyPlaceholderState(state.Value)
                    || state.Value.PlaceholderIdentity is not { Length: > 0 }
                    || explicitlyDeletedKeys.Contains(state.Key)
                    || context.LocalByPath.ContainsKey(state.Key)
                    || !context.RemoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote)
                    || !RemoteMatchesBaseline(remote.File, state.Value))
                {
                    continue;
                }

                sources.Add(new OnlineOnlyPlaceholderMoveSource(state.Key, state.Value, remote));
            }

            return sources;
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> FindOnlineOnlyPlaceholderMoveTargets(
            OnlineOnlyPlaceholderMoveContext context)
        {
            List<OnlineOnlyPlaceholderMoveTarget> targets = [];
            foreach (KeyValuePair<string, LocalFileSnapshot> local in context.LocalByPath)
            {
                if (local.Value.IsCloudFilesOnlineOnlyPlaceholder
                    && !context.StateByPath.ContainsKey(local.Key)
                    && !context.RemoteByPath.ContainsKey(local.Key))
                {
                    targets.Add(new OnlineOnlyPlaceholderMoveTarget(local.Key, local.Value));
                }
            }

            return targets;
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveMatch> FindUnambiguousOnlineOnlyPlaceholderMoveMatches(
            SyncRunOptions options,
            IReadOnlySet<string> scopedKeys,
            IReadOnlyList<OnlineOnlyPlaceholderMoveSource> sources,
            IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> targets)
        {
            List<OnlineOnlyPlaceholderMoveMatch> matches = [];
            foreach (OnlineOnlyPlaceholderMoveSource source in sources)
            {
                OnlineOnlyPlaceholderMoveTarget[] matchingTargets = targets
                    .Where(target => CanCoalesceOnlineOnlyPlaceholderMove(
                        source.State,
                        source.Remote.File,
                        target.Local,
                        CanUseScopedOnlineOnlyPlaceholderRename(
                            options,
                            scopedKeys,
                            source.SourceKey,
                            target.TargetKey,
                            source.State.RelativePath,
                            target.Local.RelativePath)))
                    .ToArray();
                if (matchingTargets.Length != 1)
                {
                    continue;
                }

                OnlineOnlyPlaceholderMoveTarget target = matchingTargets[0];
                int matchingSourceCount = sources.Count(
                    candidate => CanCoalesceOnlineOnlyPlaceholderMove(
                        candidate.State,
                        candidate.Remote.File,
                        target.Local,
                        CanUseScopedOnlineOnlyPlaceholderRename(
                            options,
                            scopedKeys,
                            candidate.SourceKey,
                            target.TargetKey,
                            candidate.State.RelativePath,
                            target.Local.RelativePath)));
                if (matchingSourceCount == 1)
                {
                    matches.Add(new OnlineOnlyPlaceholderMoveMatch(
                        source.SourceKey,
                        source.State,
                        source.Remote,
                        target.TargetKey,
                        target.Local));
                }
            }

            return matches;
        }

        private async Task ApplyOnlineOnlyPlaceholderMoveAsync(
            OnlineOnlyPlaceholderMoveContext context,
            OnlineOnlyPlaceholderMoveMatch match)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            NodeFileManifestDto? moved = await TryMoveOnlineOnlyPlaceholderRemoteFileAsync(context, match)
                .ConfigureAwait(false);
            if (moved is null)
            {
                return;
            }

            string sourcePath = match.SourceState.RelativePath;
            string targetPath = match.Local.RelativePath;
            SyncStateEntry? targetState = await fileMaterializer.CreatePlaceholderStateAsync(
                    context.SyncPair,
                    context.Options,
                    targetPath,
                    moved,
                    context.CancellationToken,
                    match.SourceState.PlaceholderHydrationState)
                .ConfigureAwait(false);
            if (targetState is null)
            {
                throw new InvalidOperationException("Cloud Files placeholder refresh returned no state for " + targetPath + ".");
            }

            context.RemoteByPath.Remove(match.SourceKey);
            context.RemoteByPath[match.TargetKey] = new RemoteFileSnapshot
            {
                RelativePath = targetPath,
                File = moved,
            };
            context.StateByPath.Remove(match.SourceKey);
            context.StateByPath[match.TargetKey] = targetState;
            await stateStore.DeleteAsync(context.SyncPair.SyncPairId, sourcePath, context.CancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(targetState, context.CancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(context.Result, context.Options, SyncActivityKind.Moved, targetPath, "Moved from " + sourcePath + ".");
        }

        private async Task<NodeFileManifestDto?> TryMoveOnlineOnlyPlaceholderRemoteFileAsync(
            OnlineOnlyPlaceholderMoveContext context,
            OnlineOnlyPlaceholderMoveMatch match)
        {
            try
            {
                return await remoteFiles
                    .MoveFileAsync(
                        context.SyncPair.RemoteRootNodeId,
                        match.Local.RelativePath,
                        match.Remote.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsPreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await fileTransfer.FindLatestRemoteFileAsync(
                        context.SyncPair,
                        match.SourceState.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    context.RemoteByPath.Remove(match.SourceKey);
                }
                else
                {
                    context.RemoteByPath[match.SourceKey] = new RemoteFileSnapshot
                    {
                        RelativePath = match.SourceState.RelativePath,
                        File = latestRemoteFile,
                    };
                }

                return null;
            }
        }

        private static bool CanCoalesceOnlineOnlyPlaceholderMove(
            SyncStateEntry sourceState,
            NodeFileManifestDto remoteFile,
            LocalFileSnapshot target,
            bool allowChangedFileName)
        {
            return (allowChangedFileName
                    || string.Equals(
                        Path.GetFileName(sourceState.RelativePath),
                        Path.GetFileName(target.RelativePath),
                        StringComparison.OrdinalIgnoreCase))
                && VirtualFilesPlaceholderAdoptionPolicy.CanAdopt(target, remoteFile);
        }

        private static bool CanUseScopedOnlineOnlyPlaceholderRename(
            SyncRunOptions options,
            IReadOnlySet<string> scopedKeys,
            string sourceKey,
            string targetKey,
            string sourcePath,
            string targetPath)
        {
            return !options.Scope.IsFull
                && scopedKeys.Contains(sourceKey)
                && scopedKeys.Contains(targetKey)
                && PathComparer.Equals(
                    GetParentPath(sourcePath),
                    GetParentPath(targetPath));
        }
    }
}
