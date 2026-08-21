// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesNativeOperationExecutor(
        IWindowsCloudFilesDiagnostics diagnostics,
        Action<TimeSpan> transientRetryDelay)
    {
        private const int HResultFileNotFound = unchecked((int)0x80070002);
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private const int HResultSharingViolation = unchecked((int)0x80070020);
        private const int HResultLockViolation = unchecked((int)0x80070021);
        private static readonly TimeSpan[] TransientPathRetryDelays =
        [
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromMilliseconds(150),
        ];

        public void ExecuteWithTransientPathRetry(
            Action operation,
            string operationName,
            string? syncPairId,
            string? localRootPath,
            string? relativePath)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    operation();
                    return;
                }
                catch (WindowsCloudFilesNativeException exception)
                    when (IsTransientPathOpenFailure(exception) && attempt < TransientPathRetryDelays.Length)
                {
                    diagnostics.Record(
                        operationName,
                        "retrying",
                        syncPairId,
                        localRootPath,
                        relativePath,
                        exception.Message,
                        exception.HResult);
                    transientRetryDelay(TransientPathRetryDelays[attempt]);
                }
            }
        }

        public void RecordFailure(
            string operation,
            string? syncPairId,
            string? localRootPath,
            string? relativePath,
            Exception exception)
        {
            diagnostics.Record(
                operation,
                "failed",
                syncPairId,
                localRootPath,
                relativePath,
                exception.Message,
                exception is WindowsCloudFilesNativeException nativeException ? nativeException.HResult : null);
        }

        public static bool IsSharingViolation(WindowsCloudFilesNativeException exception)
        {
            return exception.HResult is HResultSharingViolation or HResultLockViolation;
        }

        private static bool IsTransientPathOpenFailure(WindowsCloudFilesNativeException exception)
        {
            return exception.Operation == "CreateFile"
                && (exception.HResult == HResultFileNotFound || exception.HResult == HResultPathNotFound);
        }
    }
}
