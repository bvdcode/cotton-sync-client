// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Cli
{
    internal static class SyncCliHttpClientFactory
    {
        public static HttpClient Create()
        {
            return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);
        }
    }
}
