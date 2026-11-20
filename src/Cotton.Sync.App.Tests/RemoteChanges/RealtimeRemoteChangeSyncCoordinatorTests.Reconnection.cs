// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Realtime;
using Cotton.Sync.App.RemoteChanges;
using Microsoft.AspNetCore.SignalR;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinatorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReconnectedConnection_ChecksChangesMissedDuringServerOutage(bool exhaustAutomaticRetries)
        {
            await using RealtimeRecoveryServer server = new();
            await server.StartAsync();
            await using CottonRealtimeClient realtime = new(new InMemoryCottonTokenStore(),
                new CottonSdkOptions { BaseAddress = server.Address });
            TaskCompletionSource firstSync = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource recoveredSync = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            realtime.Closed += (_, _) => closed.TrySetResult();
            int syncCalls = 0;
            FakeSyncSupervisor supervisor = new()
            {
                OnSyncPair = (_, _) =>
                {
                    if (Interlocked.Increment(ref syncCalls) == 1)
                    {
                        firstSync.TrySetResult();
                    }
                    else
                    {
                        recoveredSync.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
            };
            RealtimeTestAuthFlow auth = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(realtime, supervisor,
                auth, DebounceInterval, connectionRetryInterval: TimeSpan.FromMilliseconds(20));
            try
            {
                await coordinator.StartAsync();
                HubCallerContext connection = await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5));
                await firstSync.Task.WaitAsync(TimeSpan.FromSeconds(5));
                server.SetAvailable(false);
                connection.GetHttpContext()!.Abort();
                await server.WaitForRejectedConnectionAsync();
                await server.SendChangeAsync("FileRenamed");
                if (exhaustAutomaticRetries)
                {
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
                }

                server.SetAvailable(true);
                await server.WaitForConnectionAsync(TimeSpan.FromSeconds(15));

                Task observed = await Task.WhenAny(recoveredSync.Task, Task.Delay(TimeSpan.FromSeconds(5)));

                Assert.That(observed, Is.SameAs(recoveredSync.Task),
                    "Restoring the event connection must check the change feed without another server event or periodic scan.");
                Assert.That(auth.RestoreCalls, exhaustAutomaticRetries ? Is.GreaterThanOrEqualTo(2) : Is.EqualTo(1));
            }
            finally
            {
                await coordinator.StopAsync();
            }
        }
    }
}
