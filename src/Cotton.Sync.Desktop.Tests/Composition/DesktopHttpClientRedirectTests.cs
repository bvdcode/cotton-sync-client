// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Sockets;
using System.Text;
using Cotton.Sync.Desktop.Composition;

namespace Cotton.Sync.Desktop.Tests.Composition
{
    public class DesktopHttpClientRedirectTests
    {
        [TestCase(302)]
        [TestCase(307)]
        [TestCase(308)]
        public async Task AuthorizedPost_DoesNotFollowRedirectToAnotherServer(int statusCode)
        {
            using TcpListener source = new(IPAddress.Loopback, 0);
            using TcpListener target = new(IPAddress.Loopback, 0);
            source.Start();
            target.Start();
            int sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            Task sourceResponse = RespondAsync(source,
                $"HTTP/1.1 {statusCode} Redirect\r\nLocation: http://127.0.0.1:{targetPort}/other\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                cancellation.Token);
            Task targetResponse = RespondAsync(target,
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellation.Token);
            try
            {
                using HttpClient client = DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(5));
                using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{sourcePort}/auth/refresh");
                request.Headers.Authorization = new("Bearer", "request-marker");
                request.Headers.Add("Cookie", "session=destination-marker");
                request.Content = new StringContent("refreshToken=body-marker");
                using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);
                await sourceResponse;
                Assert.Multiple(() =>
                {
                    Assert.That((int)response.StatusCode, Is.EqualTo(statusCode));
                    Assert.That(targetResponse.IsCompleted, Is.False,
                        "The redirect target must receive no request, headers or body.");
                });
            }
            finally
            {
                await cancellation.CancelAsync();
                if (!targetResponse.IsCompletedSuccessfully)
                {
                    Assert.ThrowsAsync<OperationCanceledException>(async () => await targetResponse);
                }
                await sourceResponse;
            }
        }

        private static async Task RespondAsync(TcpListener listener, string response, CancellationToken cancellationToken)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = connection.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string? line;
            do
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(line));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
    }
}
