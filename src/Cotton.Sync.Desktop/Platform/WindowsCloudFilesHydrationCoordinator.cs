// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesHydrationCoordinator : IWindowsCloudFilesCallbackHandler
    {
        private const int TransferBufferSize = 1024 * 1024;

        private readonly IWindowsCloudFilesRemoteContentProvider _contentProvider;
        private readonly IWindowsCloudFilesNativeApi _nativeApi;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly string _tempDirectory;

        public WindowsCloudFilesHydrationCoordinator(
            IWindowsCloudFilesRemoteContentProvider contentProvider,
            IWindowsCloudFilesNativeApi nativeApi,
            string? tempDirectory = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null)
        {
            _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
            _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _tempDirectory = string.IsNullOrWhiteSpace(tempDirectory)
                ? Path.Combine(Path.GetTempPath(), "CottonSyncCloudFiles")
                : tempDirectory;
        }

        public async Task HandleFetchDataAsync(
            WindowsCloudFilesFetchDataRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            WindowsCloudFilesPlaceholderIdentity? identity = null;
            string tempPath = CreateTempPath();

            try
            {
                identity = WindowsCloudFilesPlaceholderIdentity.Parse(request.FileIdentity);
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                await using var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    TransferBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

                await _contentProvider.DownloadAsync(identity, stream, cancellationToken).ConfigureAwait(false);
                await ValidateDownloadedContentAsync(identity, stream, cancellationToken).ConfigureAwait(false);
                await TransferRequestedRangeAsync(request, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _diagnostics.Record(
                    "hydrate",
                    "failed",
                    identity?.SyncPairId.ToString(),
                    null,
                    identity?.RelativePath ?? request.NormalizedPath,
                    exception.Message);
                _nativeApi.TransferData(WindowsCloudFilesTransferData.Failure(request));
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }

        public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
        }

        private async Task ValidateDownloadedContentAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            FileStream stream,
            CancellationToken cancellationToken)
        {
            if (identity.SizeBytes >= 0 && stream.Length != identity.SizeBytes)
            {
                throw new InvalidOperationException("Downloaded cloud-file content size does not match the placeholder identity.");
            }

            if (string.IsNullOrWhiteSpace(identity.ContentHash))
            {
                return;
            }

            stream.Position = 0;
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string actualHash = Convert.ToHexStringLower(hash);
            if (!string.Equals(actualHash, identity.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Downloaded cloud-file content hash does not match the placeholder identity.");
            }
        }

        private async Task TransferRequestedRangeAsync(
            WindowsCloudFilesFetchDataRequest request,
            FileStream stream,
            CancellationToken cancellationToken)
        {
            long start = Math.Max(0, request.RequiredOffset);
            long end = ResolveRequestedEnd(request, stream.Length);
            if (start > stream.Length || end < start)
            {
                throw new InvalidOperationException("Cloud-file hydration requested an invalid data range.");
            }

            stream.Position = start;
            long remaining = end - start;
            if (remaining == 0)
            {
                _nativeApi.TransferData(WindowsCloudFilesTransferData.Success(request, [], start, 0));
                return;
            }

            byte[] readBuffer = new byte[Math.Min(TransferBufferSize, Math.Max(1, remaining))];
            long offset = start;

            while (remaining > 0)
            {
                int requested = (int)Math.Min(readBuffer.Length, remaining);
                int read = await stream.ReadAsync(readBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Downloaded cloud-file content ended before the requested range was hydrated.");
                }

                byte[] transferBuffer = read == readBuffer.Length
                    ? readBuffer.ToArray()
                    : readBuffer.AsSpan(0, read).ToArray();
                _nativeApi.TransferData(WindowsCloudFilesTransferData.Success(request, transferBuffer, offset, read));
                offset += read;
                remaining -= read;
            }
        }

        private static long ResolveRequestedEnd(WindowsCloudFilesFetchDataRequest request, long fileSize)
        {
            if (request.RequiredLength < 0)
            {
                return fileSize;
            }

            return Math.Min(fileSize, request.RequiredOffset + request.RequiredLength);
        }

        private string CreateTempPath()
        {
            return Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
