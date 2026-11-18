// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Startup
{
    internal partial class LiveSyncManifestBarrier
    {
        private readonly Dictionary<HttpRequestMessage, long> _pendingRequests = [];

        public void RequestStarted(HttpRequestMessage request)
        {
            lock (_gate)
            {
                _pendingRequests.Add(request, Stopwatch.GetTimestamp());
            }
        }

        public void RequestFinished(HttpRequestMessage request)
        {
            lock (_gate)
            {
                _pendingRequests.Remove(request);
            }
        }

        public string ReadPendingRequests()
        {
            lock (_gate)
            {
                return string.Join("; ", _pendingRequests.Select(item =>
                    $"{item.Key.Method} {item.Key.RequestUri?.AbsolutePath} elapsed={Stopwatch.GetElapsedTime(item.Value)}"));
            }
        }
    }
}
