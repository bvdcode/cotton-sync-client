// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesHydrationCoordinator
    {
        private static string FormatHydrationRequestDetails(WindowsCloudFilesFetchDataRequest request)
        {
            WindowsCloudFilesProcessInfo? process = request.ProcessInfo;
            string requester = process is null
                ? "unknown requester"
                : "pid="
                    + process.ProcessId
                    + "; session="
                    + process.SessionId
                    + "; image="
                    + NormalizeDiagnosticValue(process.ImagePath)
                    + "; package="
                    + NormalizeDiagnosticValue(process.PackageName)
                    + "; app="
                    + NormalizeDiagnosticValue(process.ApplicationId);
            return "requiredOffset="
                + request.RequiredOffset
                + "; requiredLength="
                + request.RequiredLength
                + "; optionalOffset="
                + request.OptionalOffset
                + "; optionalLength="
                + request.OptionalLength
                + "; fileSize="
                + request.FileSizeBytes
                + "; priority="
                + request.PriorityHint
                + $"; transferKey={request.TransferKey.Value}; requestKey={request.RequestKey.Value}"
                + "; requester="
                + requester;
        }

        private static string NormalizeDiagnosticValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }

    }
}
