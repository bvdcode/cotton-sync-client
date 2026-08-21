// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.State;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal class SyncLocalContentHashResolver(
        ILocalFileContentHasher? contentHasher,
        ILocalFileContentHashProgressHasher? progressHasher)
    {
        public async Task EnsureAsync(
            LocalFileSnapshot local,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return;
            }

            if (contentHasher is null)
            {
                throw new InvalidOperationException(
                    "Local file snapshot does not include a content hash and no local content hasher is available.");
            }

            local.ContentHash = progressHasher is null
                ? await contentHasher.ComputeContentHashAsync(local, cancellationToken).ConfigureAwait(false)
                : await progressHasher
                    .ComputeContentHashAsync(local, options.TransferProgress, cancellationToken)
                    .ConfigureAwait(false);
        }

        public async Task EnsureForBaselineComparisonAsync(
            LocalFileSnapshot local,
            SyncStateEntry state,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return;
            }

            if (local.IsCloudFilesOnlineOnlyPlaceholder
                && (IsOnlineOnlyPlaceholderState(state) || IsIncompleteOnlineOnlyPlaceholderBaseline(state)))
            {
                local.ContentHash = !string.IsNullOrWhiteSpace(state.LocalContentHash)
                    ? state.LocalContentHash
                    : state.RemoteContentHash ?? string.Empty;
                return;
            }

            if (CanReuseBaselineHash(local, state))
            {
                local.ContentHash = state.LocalContentHash!;
                return;
            }

            await EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
        }

        private static bool CanReuseBaselineHash(LocalFileSnapshot local, SyncStateEntry state)
        {
            return !string.IsNullOrWhiteSpace(state.LocalContentHash)
                && state.LocalSizeBytes.HasValue
                && state.LocalSizeBytes.Value == local.SizeBytes
                && state.LocalLastWriteUtc.HasValue
                && state.LocalLastWriteUtc.Value.ToUniversalTime() == local.LastWriteUtc.ToUniversalTime();
        }
    }
}
