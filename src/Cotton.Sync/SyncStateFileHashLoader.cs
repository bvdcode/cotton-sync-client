// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.State;
using static Cotton.Sync.LocalUploadPolicy;
using static Cotton.Sync.SyncRunProgressReporter;

namespace Cotton.Sync
{
    internal class SyncStateFileHashLoader(SyncLocalContentHashResolver contentHashResolver)
    {
        public async Task LoadAsync(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (stateByPath.Count == 0)
            {
                return;
            }

            int filesTotal = stateByPath.Count(state => localByPath.ContainsKey(state.Key));
            if (filesTotal == 0)
            {
                return;
            }

            int filesCompleted = 0;
            DateTime? lastReportedAtUtc = null;
            ReportItemRunProgress(
                options,
                SyncRunProgressStage.ScanningLocal,
                filesCompleted,
                filesTotal,
                currentPath: null,
                startedAtUtc,
                ref lastReportedAtUtc);

            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (localByPath.TryGetValue(state.Key, out LocalFileSnapshot? local))
                {
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ScanningLocal,
                        filesCompleted,
                        filesTotal,
                        local.RelativePath,
                        startedAtUtc,
                        ref lastReportedAtUtc);
                    if (!ShouldDefer(local, options, out _))
                    {
                        try
                        {
                            await contentHashResolver.EnsureForBaselineComparisonAsync(
                                    local,
                                    state.Value,
                                    options,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (LocalFileUnavailableException exception)
                        {
                            ReportUnavailable(result, options, local.RelativePath, exception);
                        }
                    }

                    filesCompleted++;
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ScanningLocal,
                        filesCompleted,
                        filesTotal,
                        local.RelativePath,
                        startedAtUtc,
                        ref lastReportedAtUtc);
                }
            }
        }
    }
}
