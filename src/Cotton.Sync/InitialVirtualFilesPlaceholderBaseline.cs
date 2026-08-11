// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal readonly record struct InitialVirtualFilesPlaceholderBaseline(
        string RelativePath,
        Guid? RemoteFileId,
        string? RemoteContentHash,
        string? RemoteETag,
        string? LocalContentHash,
        long? LocalSizeBytes,
        DateTime? LocalLastWriteUtc,
        SyncPlaceholderHydrationState PlaceholderHydrationState,
        bool HasPlaceholderIdentity)
    {
        public static InitialVirtualFilesPlaceholderBaseline FromState(SyncStateEntry state)
        {
            return new InitialVirtualFilesPlaceholderBaseline(
                state.RelativePath,
                state.RemoteFileId,
                state.RemoteContentHash,
                state.RemoteETag,
                state.LocalContentHash,
                state.LocalSizeBytes,
                state.LocalLastWriteUtc,
                state.PlaceholderHydrationState,
                state.PlaceholderIdentity is { Length: > 0 });
        }

        public static InitialVirtualFilesPlaceholderBaseline FromResumeEntry(SyncVirtualFilesResumeEntry entry)
        {
            return new InitialVirtualFilesPlaceholderBaseline(
                entry.RelativePath,
                entry.RemoteFileId,
                entry.RemoteContentHash,
                entry.RemoteETag,
                LocalContentHash: null,
                LocalSizeBytes: null,
                LocalLastWriteUtc: null,
                entry.PlaceholderHydrationState,
                entry.HasPlaceholderIdentity);
        }
    }
}
