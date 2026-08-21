// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncPathOperations;
using static Cotton.Sync.SyncRunProgressReporter;

namespace Cotton.Sync
{
    internal class SyncDirectoryReconciler(
        IRemoteDirectorySynchronizer? remoteDirectories,
        ISyncStateStore stateStore,
        ILocalFileSyncWriter localWriter,
        IRemoteDirectoryMaterializationObserver? materializationObserver,
        ILogger logger)
    {
        public async Task ReconcileWithoutBaselineAsync(DirectoryReconciliationContext context)
        {
            int foldersCompleted = 0;
            DateTime? lastDirectoryRunProgressReportedAtUtc = null;
            foreach (string key in context.PathKeys)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.LocalByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                context.RemoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                context.StateByPath.TryGetValue(key, out SyncStateEntry? state);
                string relativePath = ResolveRelativePath(key, local, remote, state);
                ReportProgress(context, foldersCompleted, relativePath, ref lastDirectoryRunProgressReportedAtUtc);
                if (state is null)
                {
                    await ReconcileWithoutBaselineAsync(context, relativePath, local, remote).ConfigureAwait(false);
                }

                foldersCompleted++;
                ReportProgress(context, foldersCompleted, relativePath, ref lastDirectoryRunProgressReportedAtUtc);
            }
        }

        public async Task CreateRemoteBackedLocalDirectoryAsync(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteDirectory,
            CancellationToken cancellationToken)
        {
            RemoteDirectoryMaterializationRequest? materializationRequest = null;
            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && materializationObserver is not null)
            {
                materializationRequest = CreateRemoteDirectoryMaterializationRequest(
                    syncPair,
                    relativePath,
                    remoteDirectory);
                await materializationObserver
                    .BeforeCreateDirectoryAsync(materializationRequest, cancellationToken)
                    .ConfigureAwait(false);
            }

            await localWriter.CreateDirectoryAsync(syncPair.LocalRootPath, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (materializationRequest is not null)
            {
                await materializationObserver!
                    .AfterCreateDirectoryAsync(materializationRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public static string ResolveRelativePath(
            string key,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote,
            SyncStateEntry? state)
        {
            if (local is not null)
            {
                return local.RelativePath;
            }

            if (remote is not null)
            {
                return remote.RelativePath;
            }

            return state is not null ? state.RelativePath : key;
        }

        private async Task ReconcileWithoutBaselineAsync(
            DirectoryReconciliationContext context,
            string relativePath,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote)
        {
            if (local is null)
            {
                if (remote is null)
                {
                    return;
                }

                await CreateRemoteBackedLocalDirectoryAsync(
                        context.SyncPair,
                        relativePath,
                        remote.Node,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                await stateStore.UpsertAsync(
                        BuildDirectoryBaseline(context.SyncPair, relativePath, remote.Node),
                        context.CancellationToken)
                    .ConfigureAwait(false);
                SyncActivityReporter.Record(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Downloaded,
                    relativePath,
                    "Created local folder.");
                return;
            }

            if (remote is null)
            {
                if (remoteDirectories is not null)
                {
                    await CreateRemoteDirectoryWithoutBaselineAsync(context, relativePath).ConfigureAwait(false);
                }

                return;
            }

            await stateStore.UpsertAsync(
                    BuildDirectoryBaseline(context.SyncPair, relativePath, remote.Node),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private async Task CreateRemoteDirectoryWithoutBaselineAsync(
            DirectoryReconciliationContext context,
            string relativePath)
        {
            string parentPath = GetParentPath(relativePath);
            string parentKey = string.IsNullOrEmpty(parentPath) ? string.Empty : SyncPath.ToKey(parentPath);
            if (!TryGetRemoteDirectoryNodeId(
                    context.RemoteByPath,
                    parentKey,
                    context.RemoteRootNode.Id,
                    out Guid parentNodeId))
            {
                return;
            }

            RemoteDirectoryCreationResult creation = await CreateOrReuseRemoteDirectoryAsync(
                    remoteDirectories!,
                    parentNodeId,
                    GetFileName(relativePath),
                    context.CancellationToken)
                .ConfigureAwait(false);
            RemoteDirectorySnapshot createdSnapshot = new()
            {
                RelativePath = relativePath,
                Node = creation.Node,
            };
            context.RemoteByPath[SyncPath.ToKey(relativePath)] = createdSnapshot;
            await stateStore.UpsertAsync(
                    BuildDirectoryBaseline(context.SyncPair, relativePath, creation.Node),
                    context.CancellationToken)
                .ConfigureAwait(false);
            string details = creation.ReusedExisting
                ? "Reused existing remote folder after create conflict."
                : "Created remote folder.";
            SyncActivityReporter.Record(
                context.Result,
                context.Options,
                SyncActivityKind.Uploaded,
                relativePath,
                details);
        }

        private async Task<RemoteDirectoryCreationResult> CreateOrReuseRemoteDirectoryAsync(
            IRemoteDirectorySynchronizer synchronizer,
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            try
            {
                NodeDto created = await synchronizer
                    .CreateDirectoryAsync(parentNodeId, name, cancellationToken)
                    .ConfigureAwait(false);
                return new RemoteDirectoryCreationResult(created, ReusedExisting: false);
            }
            catch (CottonApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                NodeDto? existing = await synchronizer
                    .FindChildDirectoryAsync(parentNodeId, name, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                logger.LogInformation(
                    "Remote folder create for {DirectoryName} under {ParentNodeId} hit conflict; reusing existing node {NodeId}.",
                    name,
                    parentNodeId,
                    existing.Id);
                return new RemoteDirectoryCreationResult(existing, ReusedExisting: true);
            }
        }

        public static RemoteDirectoryMaterializationRequest CreateRemoteDirectoryMaterializationRequest(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteDirectory)
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SyncPath.Normalize(relativePath),
                remoteDirectory);
        }

        private static bool TryGetRemoteDirectoryNodeId(
            IDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            string key,
            Guid remoteRootNodeId,
            out Guid nodeId)
        {
            if (string.IsNullOrEmpty(key))
            {
                nodeId = remoteRootNodeId;
                return true;
            }

            if (remoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote))
            {
                nodeId = remote.Node.Id;
                return true;
            }

            nodeId = Guid.Empty;
            return false;
        }

        private static void ReportProgress(
            DirectoryReconciliationContext context,
            int foldersCompleted,
            string relativePath,
            ref DateTime? lastReportedAtUtc)
        {
            ReportItemRunProgress(
                context.Options,
                SyncRunProgressStage.ReconcilingDirectories,
                foldersCompleted,
                context.PathKeys.Count,
                relativePath,
                context.StartedAtUtc,
                ref lastReportedAtUtc);
        }
    }
}
