// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.Auth;
using Cotton.Sync.State;
using System.Net;

namespace Cotton.Sync.Cli
{
    public static partial class SyncCliCommandRunner
    {
        private const string AuthBrowserCommand = "auth-browser";

        private const string StateSummaryCommand = "state-summary";

        private const string SyncOnceCommand = "sync-once";

        private const string SyncSoakCommand = "sync-soak";

        private const string SyncCrudSmokeCommand = "sync-crud-smoke";

        private const int SyncOnceMaxTransientAttempts = 3;

        private static readonly TimeSpan SyncOnceInitialRetryDelay = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan SyncOnceMaxRetryDelay = TimeSpan.FromSeconds(15);
    }
}
