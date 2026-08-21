// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private async Task RecoverPersistedAvailabilityAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            List<SyncStateEntry> hydratedEntries = new(AvailabilityStateWriteBatchSize);
            Dictionary<string, SyncStateEntry> directoryEntries = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> completedDirectoryKeys = new(StringComparer.OrdinalIgnoreCase);
            int hydratedFiles = 0;
            int alreadyHydratedFiles = 0;

            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                WindowsVirtualFilesAvailabilityRecoveryOutcome outcome;
                try
                {
                    outcome = await RecoverPersistedAvailabilityEntryAsync(
                                syncPair,
                                entry,
                                hydratedEntries,
                                directoryEntries,
                                completedDirectoryKeys,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverableAvailabilityFailure(exception))
                {
                    RecordAvailabilityRecoverySkipped(syncPair, entry.RelativePath, exception);
                    continue;
                }
                switch (outcome)
                {
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored:
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.DirectoryTracked:
                        break;
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.AlreadyHydrated:
                        alreadyHydratedFiles++;
                        break;
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.Hydrated:
                        hydratedFiles++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported recovery outcome.");
                }

                if (hydratedEntries.Count >= AvailabilityStateWriteBatchSize)
                {
                    await FlushAvailabilityStateAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushAvailabilityStateAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            int completedDirectories = CompleteRecoveredDirectories(
                syncPair,
                directoryEntries,
                completedDirectoryKeys,
                cancellationToken);
            RecordAvailabilityRecoveryCompleted(
                syncPair,
                hydratedFiles,
                alreadyHydratedFiles,
                completedDirectories);
        }

        private async Task<WindowsVirtualFilesAvailabilityRecoveryOutcome> RecoverPersistedAvailabilityEntryAsync(
            SyncPairSettings syncPair,
            SyncStateEntry entry,
            ICollection<SyncStateEntry> hydratedEntries,
            IDictionary<string, SyncStateEntry> directoryEntries,
            ISet<string> completedDirectoryKeys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            if (entry.Kind == SyncEntryKind.Directory)
            {
                directoryEntries[SyncPath.ToKey(entry.RelativePath)] = entry;
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.DirectoryTracked;
            }

            if (!IsTrackedVirtualFile(entry))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
            WindowsVirtualFileDiskState? fileState = TryReadDiskState(filePath);
            if (fileState is null || !HasRawAttribute(fileState.Attributes, FileAttributePinned))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            if (IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
            {
                AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.AlreadyHydrated;
            }

            if (!IsManualAlwaysKeepCandidate(fileState.Attributes, entry.PlaceholderHydrationState))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            await HydrateTrackedPlaceholderAsync(
                    syncPair,
                    entry.RelativePath,
                    filePath,
                    entry,
                    persistState: false,
                    suppressProviderWrite: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            hydratedEntries.Add(entry);
            AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
            return WindowsVirtualFilesAvailabilityRecoveryOutcome.Hydrated;
        }

        private async Task FlushAvailabilityStateAsync(
            List<SyncStateEntry> hydratedEntries,
            CancellationToken cancellationToken)
        {
            if (hydratedEntries.Count == 0)
            {
                return;
            }

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            hydratedEntries.Clear();
        }

        private int CompleteRecoveredDirectories(
            SyncPairSettings syncPair,
            IReadOnlyDictionary<string, SyncStateEntry> directoryEntries,
            IEnumerable<string> completedDirectoryKeys,
            CancellationToken cancellationToken)
        {
            SyncStateEntry[] completedDirectories = completedDirectoryKeys
                .Select(key => directoryEntries.GetValueOrDefault(key))
                .OfType<SyncStateEntry>()
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in completedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            return completedDirectories.Length;
        }

        private void RecordAvailabilityRecoveryCompleted(
            SyncPairSettings syncPair,
            int hydratedFiles,
            int alreadyHydratedFiles,
            int completedDirectories)
        {
            _diagnostics.Record(
                "manual-always-keep-recovery",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                ".",
                "Hydrated "
                + hydratedFiles
                + " persisted pinned files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + completedDirectories
                + " tracked directories.");
        }

        private void RecordAvailabilityRecoverySkipped(
            SyncPairSettings syncPair,
            string relativePath,
            Exception exception)
        {
            _diagnostics.Record(
                "manual-always-keep-recovery",
                "skipped",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                relativePath,
                "Persisted availability recovery yielded to the primary sync: " + exception.Message,
                exception.HResult);
        }

        private static bool IsRecoverableAvailabilityFailure(Exception exception)
        {
            return exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or WindowsCloudFilesNativeException;
        }
    }
}
