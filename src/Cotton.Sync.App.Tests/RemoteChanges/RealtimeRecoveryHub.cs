// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.SignalR;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    internal class RealtimeRecoveryHub(RealtimeRecoveryServer server) : Hub
    {
        public override Task OnConnectedAsync()
        {
            server.RecordConnection(Context);
            return base.OnConnectedAsync();
        }
    }
}
