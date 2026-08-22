// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        private static SyncPairRunner CreateRunner(
            SyncPairSettings syncPair,
            ISyncPairWork? work = null,
            SyncPairRunnerRetryOptions? retryOptions = null,
            ILogger<SyncPairRunner>? logger = null)
        {
            return new SyncPairRunner(syncPair, work ?? new FakeSyncPairWork(), retryOptions, logger);
        }

        private static SyncPairRunnerRetryOptions NoDelayRetryOptions(int maxAttempts = 3)
        {
            return new SyncPairRunnerRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            };
        }

        private static SyncPairSettings CreatePair(
            bool isEnabled,
            string? localRootPath = null,
            SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = localRootPath ?? Path.GetTempPath(),
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = mode,
            };
        }
    }
}
