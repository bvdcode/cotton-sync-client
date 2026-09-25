// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Identifies one version of remote file content for a resumable download.
    /// </summary>
    public record RemoteFileDownloadIdentity(Guid NodeFileId, long? SizeBytes, string ETag);
}
