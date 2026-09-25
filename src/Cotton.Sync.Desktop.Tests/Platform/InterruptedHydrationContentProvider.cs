// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class InterruptedHydrationContentProvider : IWindowsCloudFilesRemoteContentProvider, IAsyncDisposable
    {
        private readonly byte[] _content;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly HttpClient _client = new();
        private readonly Task _server;
        private readonly Uri _address;

        public InterruptedHydrationContentProvider(byte[] content)
        {
            _content = content;
            _listener.Start();
            _address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/content");
            _server = ServeAsync(_shutdown.Token);
        }

        public int Attempts { get; private set; }

        public HttpRequestError? InterruptedError { get; private set; }

        public List<string> RequestedPaths { get; } = [];

        public async Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            RequestedPaths.Add(identity.RelativePath);
            using HttpResponseMessage response = await _client.GetAsync(
                _address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                await responseStream.CopyToAsync(destination, cancellationToken);
                transferProgress?.Report(new(SyncTransferDirection.Download, identity.RelativePath, _content.Length, _content.Length));
            }
            catch (HttpIOException exception)
            {
                InterruptedError = exception.HttpRequestError;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _server;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _client.Dispose();
                _shutdown.Dispose();
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using TcpClient connection = await _listener.AcceptTcpClientAsync(cancellationToken);
                await using NetworkStream stream = connection.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                byte[] headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {_content.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                int length = attempt == 0 ? _content.Length / 2 : _content.Length;
                await stream.WriteAsync(_content.AsMemory(0, length), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
    }
}
