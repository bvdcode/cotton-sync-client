// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        public async Task<WindowsCloudFilesUploadedFileFinalizationResult> FinalizeUploadedFileAsync(
            WindowsCloudFilesUploadedFileFinalizationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            WindowsCloudFilesNativePlaceholder placeholder = request.Placeholder;
            string filePath = Path.Combine(placeholder.BaseDirectoryPath, placeholder.RelativeFileName);
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                IntegrityHashBufferSize,
                FileOptions.SequentialScan);
            long localSizeBytes = stream.Length;
            DateTime localCreatedAtUtc = File.GetCreationTimeUtc(filePath);
            DateTime localLastWriteUtc = File.GetLastWriteTimeUtc(filePath);
            if (localSizeBytes != request.ExpectedSizeBytes
                || localLastWriteUtc != request.ExpectedLastWriteUtc.ToUniversalTime())
            {
                return new WindowsCloudFilesUploadedFileFinalizationResult(
                    IsFinalized: false,
                    localSizeBytes,
                    localLastWriteUtc);
            }

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string contentHash = Convert.ToHexStringLower(hash);
            if (!string.Equals(contentHash, request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return new WindowsCloudFilesUploadedFileFinalizationResult(
                    IsFinalized: false,
                    localSizeBytes,
                    localLastWriteUtc);
            }

            switch (request.Mode)
            {
                case WindowsCloudFilesUploadedFileFinalizationMode.ConvertRegularFile:
                    ConvertUploadedFileToPlaceholder(stream.SafeFileHandle, placeholder.FileIdentity);
                    break;
                case WindowsCloudFilesUploadedFileFinalizationMode.UpdateExistingPlaceholder:
                    UpdateUploadedFilePlaceholder(
                        stream.SafeFileHandle,
                        placeholder.FileIdentity,
                        localSizeBytes,
                        localCreatedAtUtc,
                        localLastWriteUtc);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(request.Mode),
                        request.Mode,
                        "Unsupported upload finalization mode.");
            }

            return new WindowsCloudFilesUploadedFileFinalizationResult(
                IsFinalized: true,
                localSizeBytes,
                localLastWriteUtc);
        }

        private static void ConvertUploadedFileToPlaceholder(SafeFileHandle handle, byte[] fileIdentity)
        {
            PinnedBuffer identity = PinnedBuffer.Pin(fileIdentity);
            try
            {
                int result = CfConvertToPlaceholder(
                    handle.DangerousGetHandle(),
                    identity.Pointer,
                    identity.Length,
                    CfConvertFlags.MarkInSync,
                    IntPtr.Zero,
                    IntPtr.Zero);
                ThrowIfFailed(result, nameof(CfConvertToPlaceholder));
            }
            finally
            {
                identity.Dispose();
            }
        }

        private static void UpdateUploadedFilePlaceholder(
            SafeFileHandle handle,
            byte[] identity,
            long fileSizeBytes,
            DateTime createdAtUtc,
            DateTime updatedAtUtc)
        {
            PinnedBuffer fileIdentity = PinnedBuffer.Pin(identity);
            try
            {
                CfFsMetadata metadata = CfFsMetadata.CreateFile(
                    fileSizeBytes,
                    createdAtUtc,
                    updatedAtUtc);
                int result = CfUpdatePlaceholder(
                    handle.DangerousGetHandle(),
                    ref metadata,
                    fileIdentity.Pointer,
                    fileIdentity.Length,
                    IntPtr.Zero,
                    0,
                    CfUpdateFlags.MarkInSync | CfUpdateFlags.AllowPartial,
                    IntPtr.Zero,
                    IntPtr.Zero);
                ThrowIfFailed(result, nameof(CfUpdatePlaceholder));
            }
            finally
            {
                fileIdentity.Dispose();
            }
        }
    }
}
