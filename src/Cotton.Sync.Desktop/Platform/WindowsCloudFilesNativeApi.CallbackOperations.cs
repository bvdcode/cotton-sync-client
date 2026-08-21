// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        public void HydratePlaceholder(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            int openResult = CfOpenFileWithOplock(
                WindowsNativePath.ToWin32FilePath(filePath),
                CfOpenFileFlags.None,
                out IntPtr protectedHandle);
            ThrowIfFailed(openResult, nameof(CfOpenFileWithOplock));
            try
            {
                int hydrateResult = CfHydratePlaceholder(
                    protectedHandle,
                    0,
                    -1,
                    CfHydrateFlags.None,
                    IntPtr.Zero);
                ThrowIfFailed(hydrateResult, nameof(CfHydratePlaceholder));
            }
            finally
            {
                CfCloseHandle(protectedHandle);
            }
        }

        public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            Directory.CreateDirectory(request.LocalRootPath);

            var callbackState = new NativeCallbackState(request.CallbackHandler, this);
            int result = CfConnectSyncRoot(
                request.LocalRootPath,
                callbackState.CallbackTable,
                callbackState.Context,
                CfConnectFlags.RequireProcessInfo | CfConnectFlags.BlockSelfImplicitHydration,
                out long connectionKey);
            if (result < Succeeded)
            {
                callbackState.Dispose();
                ThrowIfFailed(result, nameof(CfConnectSyncRoot));
            }

            return new WindowsCloudFilesConnection(
                request.LocalRootPath,
                new WindowsCloudFilesConnectionKey(connectionKey),
                DisconnectSyncRoot,
                callbackState);
        }

        public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
        {
            int result = CfDisconnectSyncRoot(connectionKey.Value);
            ThrowIfFailed(result, nameof(CfDisconnectSyncRoot));
        }

        public void TransferData(WindowsCloudFilesTransferData transfer)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            if (transfer.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transfer), "Cloud Files transfer length cannot be negative.");
            }

            if (transfer.CompletionStatus == WindowsCloudFilesTransferData.StatusSuccess
                && transfer.Length > transfer.Buffer.LongLength)
            {
                throw new ArgumentException("Cloud Files transfer buffer is shorter than the requested transfer length.", nameof(transfer));
            }

            PinnedBuffer buffer = PinnedBuffer.Pin(transfer.Buffer);
            try
            {
                var operationInfo = new CfOperationInfo
                {
                    StructSize = (uint)Marshal.SizeOf<CfOperationInfo>(),
                    Type = CfOperationType.TransferData,
                    ConnectionKey = transfer.ConnectionKey.Value,
                    TransferKey = transfer.TransferKey.Value,
                    CorrelationVector = IntPtr.Zero,
                    SyncStatus = IntPtr.Zero,
                    RequestKey = transfer.RequestKey.Value,
                };
                var parameters = new CfOperationTransferDataParameters
                {
                    ParamSize = (uint)Marshal.SizeOf<CfOperationTransferDataParameters>(),
                    Flags = CfOperationTransferDataFlags.None,
                    CompletionStatus = transfer.CompletionStatus,
                    Buffer = transfer.CompletionStatus == WindowsCloudFilesTransferData.StatusSuccess
                        ? buffer.Pointer
                        : IntPtr.Zero,
                    Offset = transfer.Offset,
                    Length = transfer.Length,
                };

                int result = CfExecute(ref operationInfo, ref parameters);
                ThrowIfFailed(result, nameof(CfExecute));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
        {
            ArgumentNullException.ThrowIfNull(dehydrate);

            PinnedBuffer fileIdentity = PinnedBuffer.Pin(dehydrate.FileIdentity);
            try
            {
                var operationInfo = new CfOperationInfo
                {
                    StructSize = (uint)Marshal.SizeOf<CfOperationInfo>(),
                    Type = CfOperationType.AckDehydrate,
                    ConnectionKey = dehydrate.ConnectionKey.Value,
                    TransferKey = dehydrate.TransferKey.Value,
                    CorrelationVector = IntPtr.Zero,
                    SyncStatus = IntPtr.Zero,
                    RequestKey = dehydrate.RequestKey.Value,
                };
                var parameters = new CfOperationAckDehydrateParameters
                {
                    ParamSize = (uint)Marshal.SizeOf<CfOperationAckDehydrateParameters>(),
                    Flags = CfOperationAckDehydrateFlags.None,
                    CompletionStatus = dehydrate.CompletionStatus,
                    FileIdentity = fileIdentity.Pointer,
                    FileIdentityLength = fileIdentity.Length,
                };

                int result = CfExecuteAckDehydrate(ref operationInfo, ref parameters);
                ThrowIfFailed(result, nameof(CfExecute));
            }
            finally
            {
                fileIdentity.Dispose();
            }
        }

        public void DehydratePlaceholder(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            int openResult = CfOpenFileWithOplock(
                WindowsNativePath.ToWin32FilePath(filePath),
                CfOpenFileFlags.Exclusive,
                out IntPtr protectedHandle);
            ThrowIfFailed(openResult, nameof(CfOpenFileWithOplock));
            try
            {
                int dehydrateResult = CfDehydratePlaceholder(
                    protectedHandle,
                    0,
                    -1,
                    CfDehydrateFlags.None,
                    IntPtr.Zero);
                ThrowIfFailed(dehydrateResult, nameof(CfDehydratePlaceholder));
            }
            finally
            {
                CfCloseHandle(protectedHandle);
            }
        }

        public async Task<bool> DehydratePlaceholderIfContentMatchesAsync(
            string filePath,
            string expectedContentHash,
            Action? contentValidated,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                IntegrityHashBufferSize,
                FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string contentHash = Convert.ToHexStringLower(hash);
            if (!string.Equals(contentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            contentValidated?.Invoke();
            int dehydrateResult = CfDehydratePlaceholder(
                stream.SafeFileHandle.DangerousGetHandle(),
                0,
                -1,
                CfDehydrateFlags.None,
                IntPtr.Zero);
            ThrowIfFailed(dehydrateResult, nameof(CfDehydratePlaceholder));
            return true;
        }
    }
}
