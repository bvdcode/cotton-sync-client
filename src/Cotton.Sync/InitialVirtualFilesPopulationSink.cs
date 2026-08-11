// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Threading.Channels;
using Cotton.Sync.Remote;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesPopulationSink(
        ChannelWriter<InitialVirtualFilesPopulationItem> writer,
        InitialVirtualFilesPopulationMetrics metrics) : IRemoteTreeStreamSink
    {
        private readonly ChannelWriter<InitialVirtualFilesPopulationItem> _writer =
            writer ?? throw new ArgumentNullException(nameof(writer));
        private readonly InitialVirtualFilesPopulationMetrics _metrics =
            metrics ?? throw new ArgumentNullException(nameof(metrics));

        public async ValueTask AddDirectoryAsync(
            RemoteDirectorySnapshot directory,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(directory);
            await _writer.WriteAsync(new InitialVirtualFilesDirectoryPopulationItem(directory), cancellationToken)
                .ConfigureAwait(false);
            _metrics.RecordDiscoveredDirectory();
        }

        public async ValueTask AddFileAsync(RemoteFileSnapshot file, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            await _writer.WriteAsync(new InitialVirtualFilesFilePopulationItem(file), cancellationToken)
                .ConfigureAwait(false);
            _metrics.RecordDiscoveredFile();
        }
    }
}
