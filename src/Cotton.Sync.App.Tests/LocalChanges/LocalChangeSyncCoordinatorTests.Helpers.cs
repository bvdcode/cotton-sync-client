// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public partial class LocalChangeSyncCoordinatorTests
    {
        private static SyncPairSettings CreatePair(bool isEnabled, SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = mode,
            };
        }

        private static string FullPath(SyncPairSettings syncPair, string relativePath)
        {
            return Path.Combine(
                syncPair.LocalRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static async Task WaitForCanceledStormAsync(Task storm)
        {
            try
            {
                await storm.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

    }
}
