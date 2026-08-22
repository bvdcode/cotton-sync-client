// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync;
using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliSoakCommandRunner
    {
        private const int MaxFinalConvergencePasses = 6;

        private static readonly TimeSpan SoakMinimumLocalUploadAge = TimeSpan.FromSeconds(3);
    }
}
