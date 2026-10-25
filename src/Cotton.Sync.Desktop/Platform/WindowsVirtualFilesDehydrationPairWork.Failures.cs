// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private async Task<bool> TryHandleDehydrationWithFailureIsolationAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string relativePath,
            Action<string>? dehydrationStarting,
            CancellationToken cancellationToken)
        {
            try
            {
                return await TryHandleManualDehydrationAsync(
                        syncPair, relativePath, dehydrationStarting, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WindowsCloudFilesNativeException exception)
                when (exception.Operation == "CfDehydratePlaceholder"
                    && WindowsCloudFilesNativeOperationExecutor.IsFileSystemLimitation(exception))
            {
                _diagnostics.Record(
                    "manual-free-up-space",
                    "deferred",
                    syncPair.Id.ToString("D"),
                    syncPair.LocalRootPath,
                    relativePath,
                    "Windows could not free up local space. Tracked file state was preserved; continuing synchronization. "
                    + exception.Message,
                    exception.HResult);
                run.DeferredDehydrationPaths.Add(relativePath);
                return false;
            }
        }
    }
}
