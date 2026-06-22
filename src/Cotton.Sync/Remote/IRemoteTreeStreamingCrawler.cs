// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Crawls remote Cotton folders while streaming entries to a backpressured consumer.
    /// </summary>
    public interface IRemoteTreeStreamingCrawler : IRemoteTreeCrawler
    {
        /// <summary>
        /// Crawls a remote root and streams entries as each page is discovered.
        /// </summary>
        Task<NodeDto> CrawlStreamingAsync(
            Guid rootNodeId,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default);
    }
}
