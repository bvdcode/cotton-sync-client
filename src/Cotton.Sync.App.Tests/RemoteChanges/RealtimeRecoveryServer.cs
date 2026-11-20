// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    internal class RealtimeRecoveryServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly Channel<HubCallerContext> _connections = Channel.CreateUnbounded<HubCallerContext>();
        private readonly TaskCompletionSource _rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _available = true;

        public RealtimeRecoveryServer()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(this);
            builder.Services.AddSignalR();
            _application = builder.Build();
            _application.Use(async (context, next) =>
            {
                if (!_available)
                {
                    _rejected.TrySetResult();
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    return;
                }
                await next(context);
            });
            _application.MapHub<RealtimeRecoveryHub>("/api/v1/hub/events");
        }

        public Uri Address => new(_application.Urls.Single());

        public Task StartAsync() => _application.StartAsync();

        public void SetAvailable(bool available) => _available = available;

        public void RecordConnection(HubCallerContext context) => _connections.Writer.TryWrite(context);

        public async Task<HubCallerContext> WaitForConnectionAsync(TimeSpan timeout)
        {
            using CancellationTokenSource cancellation = new(timeout);
            return await _connections.Reader.ReadAsync(cancellation.Token);
        }

        public Task WaitForRejectedConnectionAsync() => _rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task SendChangeAsync(string method)
        {
            return _application.Services.GetRequiredService<IHubContext<RealtimeRecoveryHub>>()
                .Clients.All.SendCoreAsync(method, [null]);
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }
}
