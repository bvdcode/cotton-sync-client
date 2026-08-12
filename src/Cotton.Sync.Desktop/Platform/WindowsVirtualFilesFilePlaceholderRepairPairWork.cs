// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsVirtualFilesFilePlaceholderRepairPairWork : ISyncPairWork
    {
        private const int HResultFileNotFound = unchecked((int)0x80070002);
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private const int ProgressInterval = 512;

        private readonly ISyncPairWork _inner;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly IAppRunProgressPublisher? _runProgressPublisher;

        public WindowsVirtualFilesFilePlaceholderRepairPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalChangeSuppression? localChangeSuppression = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _localChangeSuppression = localChangeSuppression;
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _runProgressPublisher = runProgressPublisher;
        }

        public Task RunOnceAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken = default)
        {
            return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
        }

        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles || !request.IsFull)
            {
                return;
            }

            await RepairTrackedFilePlaceholdersAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
        }

        private async Task RepairTrackedFilePlaceholdersAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            FilePlaceholderRepairStatistics statistics = new();
            PublishProgress(syncPair.Id, request, statistics.StartedAt, 0, null, isCompleted: false);

            try
            {
                using IDisposable? burst = _localChangeSuppression?.SuppressProviderWriteBurst(
                    syncPair.Id,
                    syncPair.LocalRootPath);
                await RepairTrackedEntriesAsync(syncPair, request, statistics, cancellationToken).ConfigureAwait(false);

                if (statistics.RepairedCount > 0)
                {
                    _cloudFiles.SetSyncRootInSyncState(syncPair);
                }
            }
            catch (Exception exception)
            {
                CompleteRepair(syncPair, request, statistics, "failed", GetNativeHResult(exception));
                throw;
            }

            CompleteRepair(syncPair, request, statistics, "completed");
        }

        private async Task RepairTrackedEntriesAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            FilePlaceholderRepairStatistics statistics,
            CancellationToken cancellationToken)
        {
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTrackedPlaceholderFile(entry))
                {
                    continue;
                }

                statistics.RecordCandidate();
                bool inspectedPlaceholder = RepairTrackedEntry(syncPair, entry, statistics);
                if (inspectedPlaceholder && statistics.CandidateCount % ProgressInterval == 0)
                {
                    PublishProgress(
                        syncPair.Id,
                        request,
                        statistics.StartedAt,
                        statistics.CandidateCount,
                        totalCount: null,
                        isCompleted: false);
                }
            }
        }

        private bool RepairTrackedEntry(
            SyncPairSettings syncPair,
            SyncStateEntry entry,
            FilePlaceholderRepairStatistics statistics)
        {
            string relativePath = SyncPath.Normalize(entry.RelativePath);
            if (!File.Exists(GetFullPath(syncPair.LocalRootPath, relativePath)))
            {
                statistics.RecordMissing();
                return false;
            }

            WindowsCloudFilesPlaceholderState? state = TryGetPlaceholderState(syncPair, relativePath);
            if (!state.HasValue)
            {
                statistics.RecordMissing();
                return false;
            }

            if (state.Value == WindowsCloudFilesPlaceholderState.Invalid
                || !state.Value.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder))
            {
                statistics.RecordNonPlaceholder();
                return true;
            }

            bool isInSync = state.Value.HasFlag(WindowsCloudFilesPlaceholderState.InSync);
            bool repaired = false;
            if (isInSync)
            {
                byte[] nativeIdentity = _cloudFiles.GetPlaceholderIdentity(syncPair, relativePath);
                if (!nativeIdentity.AsSpan().SequenceEqual(entry.PlaceholderIdentity))
                {
                    _localChangeSuppression?.SuppressProviderWrite(
                        syncPair.Id,
                        syncPair.LocalRootPath,
                        relativePath);
                    _cloudFiles.UpdatePlaceholderIdentity(
                        syncPair,
                        relativePath,
                        entry.PlaceholderIdentity!);
                    repaired = true;
                }
            }
            else
            {
                _localChangeSuppression?.SuppressProviderWrite(
                    syncPair.Id,
                    syncPair.LocalRootPath,
                    relativePath);
                _cloudFiles.SetInSyncState(syncPair, relativePath);
                repaired = true;
            }

            if (repaired)
            {
                statistics.RecordRepaired();
            }

            return true;
        }

        private WindowsCloudFilesPlaceholderState? TryGetPlaceholderState(
            SyncPairSettings syncPair,
            string relativePath)
        {
            try
            {
                return _cloudFiles.GetPlaceholderState(syncPair, relativePath);
            }
            catch (WindowsCloudFilesNativeException exception) when (IsMissingPath(exception.HResult))
            {
                return null;
            }
        }

        private void CompleteRepair(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            FilePlaceholderRepairStatistics statistics,
            string status,
            int? hResult = null)
        {
            statistics.Stop();
            PublishProgress(
                syncPair.Id,
                request,
                statistics.StartedAt,
                statistics.CandidateCount,
                statistics.CandidateCount,
                isCompleted: true);
            RecordSummary(
                syncPair,
                status,
                statistics.CandidateCount,
                statistics.RepairedCount,
                statistics.MissingCount,
                statistics.NonPlaceholderCount,
                statistics.ElapsedMilliseconds,
                hResult);
        }

        private static int? GetNativeHResult(Exception exception)
        {
            return exception is WindowsCloudFilesNativeException nativeException
                ? nativeException.HResult
                : null;
        }

        private void PublishProgress(
            Guid syncPairId,
            SyncRunRequest request,
            DateTime startedAtUtc,
            int completedCount,
            int? totalCount,
            bool isCompleted)
        {
            _runProgressPublisher?.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                completedCount,
                totalCount,
                string.Empty,
                startedAtUtc,
                isCompleted,
                DateTime.UtcNow,
                causes: request.Causes,
                isFull: request.IsFull,
                requestedPathCount: 0));
        }

        private void RecordSummary(
            SyncPairSettings syncPair,
            string status,
            int candidateCount,
            int repairedCount,
            int missingCount,
            int nonPlaceholderCount,
            long elapsedMilliseconds,
            int? hResult = null)
        {
            _diagnostics.Record(
                "repair-file-placeholder-in-sync",
                status,
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                null,
                $"Tracked placeholder candidates={candidateCount}; repaired={repairedCount}; missing={missingCount}; non-placeholders={nonPlaceholderCount}; elapsed={elapsedMilliseconds} ms.",
                hResult);
        }

        private static bool IsTrackedPlaceholderFile(SyncStateEntry entry)
        {
            return entry.Kind == SyncEntryKind.File
                && !string.IsNullOrWhiteSpace(entry.RelativePath)
                && entry.PlaceholderIdentity is { Length: > 0 };
        }

        private static string GetFullPath(string localRootPath, string relativePath)
        {
            return Path.Combine(
                localRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool IsMissingPath(int hResult)
        {
            return hResult == HResultFileNotFound || hResult == HResultPathNotFound;
        }
    }
}
