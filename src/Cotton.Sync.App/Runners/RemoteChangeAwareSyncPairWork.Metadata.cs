// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Runners
{
    public partial class RemoteChangeAwareSyncPairWork
    {
        private async Task<SyncRunRequest> AddPendingMetadataPathsAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles || _stateStore is null)
            {
                return request;
            }

            List<string> pendingPaths = [];
            await foreach (string path in _stateStore
                               .LoadPendingPlaceholderConversionPathsAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                pendingPaths.Add(path);
                if (pendingPaths.Count > SyncRunRequest.MaximumQueuedScopedPaths)
                {
                    return RestoreApproval(request, request.Merge(SyncRunRequest.ForFull(SyncRunCause.InternalMaintenance)));
                }
            }

            return pendingPaths.Count == 0
                ? request
                : RestoreApproval(request, request.Merge(SyncRunRequest.ForLocalChangedPaths(pendingPaths)));
        }

        private static SyncRunRequest RestoreApproval(SyncRunRequest original, SyncRunRequest merged)
        {
            return original.ApprovedRemoteDeletePlan is null
                ? merged
                : merged.WithApprovedRemoteDeletePlan(original.ApprovedRemoteDeletePlan);
        }

        private static void ThrowPendingMetadataFailure(IReadOnlyList<SyncFileMetadataPendingException> failures)
        {
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new SyncFileMetadataPendingException(string.Join(
                    Environment.NewLine + Environment.NewLine,
                    failures.Select(static failure => failure.Message).Distinct(StringComparer.Ordinal)));
            }
        }
    }
}
