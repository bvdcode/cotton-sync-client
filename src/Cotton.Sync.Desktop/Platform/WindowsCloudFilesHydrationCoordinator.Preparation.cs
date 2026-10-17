// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesHydrationCoordinator
    {
        private readonly object _preparedContentGate = new();
        private readonly Dictionary<WindowsCloudFilesPlaceholderIdentity, List<FileStream>> _preparedContent = [];

        public async Task<IDisposable> PrepareHydrationAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            cancellationToken.ThrowIfCancellationRequested();
            string tempPath = CreateTempPath();
            Directory.CreateDirectory(_tempDirectory);
            FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, TransferBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            IProgress<SyncTransferProgress>? progress = null;
            bool retained = false;
            try
            {
                progress = _transferProgressFactory(identity.SyncPairId);
                await DownloadWithRetryAsync(identity, stream,
                    () => _contentProvider.DownloadAsync(identity, stream, progress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await ValidateDownloadedContentAsync(identity, stream, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_preparedContentGate)
                {
                    if (!_preparedContent.TryGetValue(identity, out List<FileStream>? streams))
                    {
                        streams = [];
                        _preparedContent.Add(identity, streams);
                    }

                    streams.Add(stream);
                }

                WindowsCloudFilesPreparedContent prepared = new(stream, ready => ReleasePreparedContent(identity, ready));
                retained = true;
                return prepared;
            }
            catch (Exception exception)
            {
                string status = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "canceled" : "failed";
                _diagnostics.Record("hydrate-prepare", status, identity.SyncPairId.ToString(),
                    null, identity.RelativePath, exception.Message);
                throw;
            }
            finally
            {
                CompleteTransferProgress(progress);
                if (!retained)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    TryDeleteTempFile(tempPath);
                }
            }
        }

        private async Task<bool> TryTransferPreparedContentAsync(
            WindowsCloudFilesFetchDataRequest request,
            WindowsCloudFilesPlaceholderIdentity identity,
            CancellationToken cancellationToken)
        {
            await using FileStream? stream = OpenPreparedContent(identity);
            if (stream is null)
            {
                return false;
            }

            await TransferRequestedRangeAsync(request, stream, cancellationToken).ConfigureAwait(false);
            TryMarkInSync(request, identity, stream.Length);
            return true;
        }

        private FileStream? OpenPreparedContent(WindowsCloudFilesPlaceholderIdentity identity)
        {
            lock (_preparedContentGate)
            {
                if (!_preparedContent.TryGetValue(identity, out List<FileStream>? streams))
                {
                    return null;
                }

                return new FileStream(streams[0].Name, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, TransferBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
        }

        private void ReleasePreparedContent(WindowsCloudFilesPlaceholderIdentity identity, FileStream stream)
        {
            lock (_preparedContentGate)
            {
                List<FileStream> streams = _preparedContent[identity];
                streams.Remove(stream);
                if (streams.Count == 0)
                {
                    _preparedContent.Remove(identity);
                }

                stream.Dispose();
            }
        }
    }
}
