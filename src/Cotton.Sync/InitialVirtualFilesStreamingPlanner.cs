// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesStreamingPlanner(
        IRemoteTreeStreamingCrawler? remoteStreamingCrawler,
        IRemoteFilePlaceholderWriter? placeholderWriter,
        ISyncStateStore stateStore,
        ILocalFileMetadataPathLookupScanner? localPathScanner,
        SyncTreeScanner treeScanner,
        ILogger logger)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task<InitialVirtualFilesStreamingPlanDecision> CreateDecisionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (!CanRun(syncPair, options))
            {
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            InitialVirtualFilesStreamingPlanDecision? stateFirstDecision =
                await TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(syncPair, cancellationToken)
                    .ConfigureAwait(false);
            if (stateFirstDecision is not null)
            {
                return stateFirstDecision;
            }

            return await InspectLocalTreeForInitialWindowsVirtualFilesStreamingDecisionAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken).ConfigureAwait(false);
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision> CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
            SyncPair syncPair,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, LocalFileSnapshot> localFilesByPath,
            CancellationToken cancellationToken)
        {
            if (localDirectoriesByPath.Count == 0 && localFilesByPath.Count == 0)
            {
                return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                    new InitialVirtualFilesStreamingPlan(
                        SkipCurrentPlaceholders: false,
                        CurrentPlaceholderBaselineByPath: new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer),
                        AdoptableUntrackedPlaceholderByPath: new Dictionary<string, LocalFileSnapshot>(PathComparer)));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            (Dictionary<string, Guid?> directoryStateByPath, Dictionary<string, InitialVirtualFilesPlaceholderBaseline> fileBaselineByPath) =
                await LoadInitialVirtualFilesResumeStateByPathAsync(
                        syncPair.SyncPairId,
                        localDirectoriesByPath.Keys.Concat(localFilesByPath.Keys),
                        cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            logger.LogInformation(
                "Loaded Windows virtual-files resume state matching the local tree for pair {SyncPairId} with {DirectoryStateCount} directories and {FileStateCount} files in {ElapsedMilliseconds} ms.",
                syncPair.SyncPairId,
                directoryStateByPath.Count,
                fileBaselineByPath.Count,
                stopwatch.ElapsedMilliseconds);
            Dictionary<string, LocalFileSnapshot> adoptableUntrackedPlaceholderByPath = new Dictionary<string, LocalFileSnapshot>(PathComparer);
            foreach ((string fileKey, LocalFileSnapshot local) in localFilesByPath)
            {
                if (fileBaselineByPath.TryGetValue(fileKey, out InitialVirtualFilesPlaceholderBaseline baseline)
                    && IsResumeCompatibleVirtualFilesPlaceholder(local, baseline))
                {
                    continue;
                }

                if (IsUntrackedVirtualFilesPlaceholderCompatibleWithInitialStreaming(local))
                {
                    adoptableUntrackedPlaceholderByPath[fileKey] = local;
                    continue;
                }

                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            foreach (string directoryKey in localDirectoriesByPath.Keys)
            {
                if (directoryStateByPath.TryGetValue(directoryKey, out Guid? remoteNodeId)
                    && remoteNodeId is null)
                {
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }

                if (!directoryStateByPath.ContainsKey(directoryKey)
                    && !HasAdoptablePlaceholderDescendant(directoryKey, adoptableUntrackedPlaceholderByPath.Keys))
                {
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }
            }

            return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                new InitialVirtualFilesStreamingPlan(
                    SkipCurrentPlaceholders: true,
                    CurrentPlaceholderBaselineByPath: fileBaselineByPath,
                    AdoptableUntrackedPlaceholderByPath: adoptableUntrackedPlaceholderByPath));
        }

        public bool CanRun(SyncPair syncPair, SyncRunOptions options)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && options.Scope.IsFull
                && options.AllowInitialVirtualFilesStreaming
                && remoteStreamingCrawler is not null
                && placeholderWriter is not null;
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision?> TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(
            SyncPair syncPair,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            InitialVirtualFilesStateFirstInspection inspection = new InitialVirtualFilesStateFirstInspection();
            await foreach (SyncStateEntry entry in stateStore
                               .LoadPairEntriesAsync(syncPair.SyncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await stateStore.DeleteAsync(syncPair.SyncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string? issue = inspection.Add(entry);
                if (issue is not null)
                {
                    LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, issue);
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }
            }

            if (inspection.EntriesSeen == 0)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, "no persisted state entries");
                return null;
            }

            if (localPathScanner is null)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, "local path metadata lookup is unavailable");
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            LocalTreeLookupSnapshot localStateLookups = await localPathScanner
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    inspection.StateRelativePaths,
                    progress: null,
                    includeDirectoryDescendants: false,
                    cancellationToken)
                .ConfigureAwait(false);
            string? incompatibility = inspection.FindLocalIncompatibility(localStateLookups);
            if (incompatibility is not null)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, incompatibility);
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            stopwatch.Stop();
            logger.LogInformation(
                "Loaded Windows virtual-files state-first resume plan for pair {SyncPairId} with {DirectoryStateCount} directories and {FileStateCount} files ({OnlineOnlyFileStateCount} online-only, {MaterializedFileStateCount} materialized) in {ElapsedMilliseconds} ms without scanning the local placeholder tree.",
                syncPair.SyncPairId,
                inspection.DirectoryEntries,
                inspection.FileEntries,
                inspection.OnlineOnlyFileEntries,
                inspection.MaterializedFileEntries,
                stopwatch.ElapsedMilliseconds);
            return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                new InitialVirtualFilesStreamingPlan(
                    SkipCurrentPlaceholders: true,
                    CurrentPlaceholderBaselineByPath: inspection.FileBaselineByPath,
                    AdoptableUntrackedPlaceholderByPath: new Dictionary<string, LocalFileSnapshot>(PathComparer)));
        }

        private void LogStateFirstPlanSkipped(
            SyncPair syncPair,
            InitialVirtualFilesStateFirstInspection inspection,
            Stopwatch stopwatch,
            string reason)
        {
            stopwatch.Stop();
            logger.LogInformation(
                "Skipping Windows virtual-files state-first resume plan for pair {SyncPairId}: {Reason}. Entries seen={EntryStateCount}, directories={DirectoryStateCount}, files={FileStateCount}, online-only files={OnlineOnlyFileStateCount}, materialized files={MaterializedFileStateCount}, elapsed={ElapsedMilliseconds} ms.",
                syncPair.SyncPairId,
                reason,
                inspection.EntriesSeen,
                inspection.DirectoryEntries,
                inspection.FileEntries,
                inspection.OnlineOnlyFileEntries,
                inspection.MaterializedFileEntries,
                stopwatch.ElapsedMilliseconds);
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision> InspectLocalTreeForInitialWindowsVirtualFilesStreamingDecisionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            SyncRunOptions silentPreflightOptions = CloneWithoutRunProgress(options);
            LocalTreeLookupSnapshot? localTreeLookups = await treeScanner.ScanLocalTreeLookupsAsync(
                    syncPair.LocalRootPath,
                    silentPreflightOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (localTreeLookups is not null)
            {
                return await CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
                        syncPair,
                        localTreeLookups.DirectoriesByPath,
                        localTreeLookups.FilesByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            LocalTreeSnapshot localTree = await treeScanner.ScanLocalTreeAsync(
                    syncPair.LocalRootPath,
                    silentPreflightOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, LocalDirectorySnapshot> directoriesByPath = localTree.Directories.ToDictionary(
                directory => SyncPath.ToKey(directory.RelativePath),
                PathComparer);
            Dictionary<string, LocalFileSnapshot> filesByPath = localTree.Files.ToDictionary(
                file => SyncPath.ToKey(file.RelativePath),
                PathComparer);
            return await CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
                    syncPair,
                    directoriesByPath,
                    filesByPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<(
            Dictionary<string, Guid?> DirectoryRemoteNodeIdByPath,
            Dictionary<string, InitialVirtualFilesPlaceholderBaseline> FileBaselineByPath)> LoadInitialVirtualFilesResumeStateByPathAsync(
            string syncPairId,
            IEnumerable<string> keys,
            CancellationToken cancellationToken)
        {
            Dictionary<string, Guid?> directoryStateByPath = new Dictionary<string, Guid?>(PathComparer);
            Dictionary<string, InitialVirtualFilesPlaceholderBaseline> fileBaselineByPath = new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer);
            if (stateStore is IVirtualFilesResumeStateStore virtualFilesResumeStateStore)
            {
                await foreach (SyncVirtualFilesResumeEntry entry in virtualFilesResumeStateStore.LoadVirtualFilesResumeEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                    {
                        await stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    string stateKey = SyncPath.ToKey(entry.RelativePath);
                    switch (entry.Kind)
                    {
                        case SyncEntryKind.Directory:
                            directoryStateByPath[stateKey] = entry.RemoteNodeId;
                            break;
                        case SyncEntryKind.File:
                            fileBaselineByPath[stateKey] = InitialVirtualFilesPlaceholderBaseline.FromResumeEntry(entry);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                    }
                }

                return (directoryStateByPath, fileBaselineByPath);
            }

            await foreach (SyncStateEntry entry in stateStore.LoadEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string stateKey = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[stateKey] = entry.RemoteNodeId;
                        break;
                    case SyncEntryKind.File:
                        fileBaselineByPath[stateKey] = InitialVirtualFilesPlaceholderBaseline.FromState(entry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            return (directoryStateByPath, fileBaselineByPath);
        }

        private static bool IsUntrackedVirtualFilesPlaceholderCompatibleWithInitialStreaming(LocalFileSnapshot local)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder;
        }

        private static bool HasAdoptablePlaceholderDescendant(
            string directoryKey,
            IEnumerable<string> adoptablePlaceholderPathKeys)
        {
            string directoryPrefix = directoryKey.TrimEnd('/') + "/";
            return adoptablePlaceholderPathKeys.Any(pathKey =>
                pathKey.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsResumeCompatibleVirtualFilesPlaceholder(
            LocalFileSnapshot local,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsResumeCompatible(local, baseline);
        }

        private static SyncRunOptions CloneWithoutRunProgress(SyncRunOptions options)
        {
            return new SyncRunOptions
            {
                Scope = options.Scope,
                DeleteRemotePermanently = options.DeleteRemotePermanently,
                MinimumLocalUploadAge = options.MinimumLocalUploadAge,
                MaximumLocalDeletesPerRun = options.MaximumLocalDeletesPerRun,
                MaximumRemoteDeletesPerRun = options.MaximumRemoteDeletesPerRun,
                ApprovedRemoteDeletePlan = options.ApprovedRemoteDeletePlan,
                MaximumStoredResultActivities = options.MaximumStoredResultActivities,
                InitialVirtualFilesPopulationQueueCapacity = options.InitialVirtualFilesPopulationQueueCapacity,
                InitialVirtualFilesStateBatchSize = options.InitialVirtualFilesStateBatchSize,
                InitialVirtualFilesPlaceholderConcurrency = options.InitialVirtualFilesPlaceholderConcurrency,
                InitialVirtualFilesPlaceholderBatchSize = options.InitialVirtualFilesPlaceholderBatchSize,
                ActivityProgress = options.ActivityProgress,
                TransferProgress = options.TransferProgress,
                CooperativeYieldAsync = options.CooperativeYieldAsync,
            };
        }
    }
}
