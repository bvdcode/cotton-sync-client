// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;

namespace Cotton.Sync
{
    internal static class RemoteSyncErrorClassifier
    {
        public static bool IsPreconditionFailed(HttpRequestException exception)
        {
            return exception.StatusCode == HttpStatusCode.PreconditionFailed;
        }

        public static bool IsConflict(HttpRequestException exception)
        {
            return exception.StatusCode == HttpStatusCode.Conflict;
        }
    }
}
