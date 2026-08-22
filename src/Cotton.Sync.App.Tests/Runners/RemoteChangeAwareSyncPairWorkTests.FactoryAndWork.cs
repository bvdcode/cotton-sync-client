// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        private static SyncPairSettings CreateSyncPair(SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = mode,
            };
        }

        private class FakeSyncPairWork : ISyncPairWork
        {
            public int RunCallCount { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public bool ThrowOnRun { get; set; }

            public Func<int, SyncRunRequest, Task>? OnRunAsync { get; set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                RunCallCount++;
                LastRequest = request;
                Requests.Add(request);
                if (ThrowOnRun)
                {
                    throw new InvalidOperationException("Inner work failed.");
                }

                if (OnRunAsync is not null)
                {
                    await OnRunAsync(RunCallCount, request).ConfigureAwait(false);
                }
            }
        }
    }
}
