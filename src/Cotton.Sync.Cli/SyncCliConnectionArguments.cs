// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Cli
{
    internal class SyncCliConnectionArguments
    {
        public string? DatabasePath { get; init; }

        public string? LocalRoot { get; init; }

        public string? Password { get; init; }

        public string? RemotePath { get; init; }

        public string? RemoteRoot { get; init; }

        public string? Server { get; init; }

        public string? SyncPairId { get; init; }

        public string? TwoFactorCode { get; init; }

        public bool UseBrowserLogin { get; init; }

        public string? Username { get; init; }
    }
}
