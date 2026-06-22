// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Sockets;

namespace Cotton.Sync.Desktop.Composition
{
    internal static class DesktopHttpClientFactory
    {
        private static readonly TimeSpan ConnectFallbackDelay = TimeSpan.FromMilliseconds(250);

        public static HttpClient Create(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            return new HttpClient(CreateHandler(), disposeHandler: true)
            {
                Timeout = timeout,
            };
        }

        private static SocketsHttpHandler CreateHandler()
        {
            return new SocketsHttpHandler
            {
                ConnectCallback = ConnectAsync,
            };
        }

        private static async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<IPAddress> orderedAddresses = OrderAddressesForConnect(addresses);
            Exception? lastException = null;
            var attempts = new List<ConnectAttempt>();
            try
            {
                foreach (IPAddress address in orderedAddresses)
                {
                    attempts.Add(StartConnectAttempt(address, context.DnsEndPoint.Port, cancellationToken));
                    ConnectAttempt? attempt = await WaitForCompletedConnectOrFallbackDelayAsync(
                            attempts,
                            ConnectFallbackDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (attempt is not null)
                    {
                        if (attempt.ConnectTask.IsCompletedSuccessfully)
                        {
                            return attempt.CreateStream();
                        }

                        lastException = attempt.ConnectTask.Exception?.GetBaseException();
                        attempt.Dispose();
                    }
                }

                while (attempts.Count > 0)
                {
                    ConnectAttempt completedAttempt = await WaitForCompletedConnectAsync(attempts, cancellationToken)
                        .ConfigureAwait(false);
                    if (completedAttempt.ConnectTask.IsCompletedSuccessfully)
                    {
                        return completedAttempt.CreateStream();
                    }

                    lastException = completedAttempt.ConnectTask.Exception?.GetBaseException();
                    completedAttempt.Dispose();
                }
            }
            finally
            {
                foreach (ConnectAttempt attempt in attempts)
                {
                    attempt.Dispose();
                }
            }

            throw lastException ?? new SocketException((int)SocketError.HostNotFound);
        }

        internal static IReadOnlyList<IPAddress> OrderAddressesForConnect(IEnumerable<IPAddress> addresses)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            return addresses
                .Where(static address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToArray();
        }

        private static ConnectAttempt StartConnectAttempt(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            return new ConnectAttempt(
                address,
                socket,
                socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).AsTask());
        }

        private static async Task<ConnectAttempt> WaitForCompletedConnectAsync(
            List<ConnectAttempt> attempts,
            CancellationToken cancellationToken)
        {
            Task completedTask = await Task.WhenAny(attempts.Select(static attempt => attempt.ConnectTask))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return RemoveCompletedAttempt(attempts, completedTask);
        }

        internal static async Task<ConnectAttempt?> WaitForCompletedConnectOrFallbackDelayAsync(
            List<ConnectAttempt> attempts,
            TimeSpan fallbackDelay,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(attempts);
            ArgumentOutOfRangeException.ThrowIfLessThan(fallbackDelay, TimeSpan.Zero);
            Task<Task> completedConnectTask = Task.WhenAny(attempts.Select(static attempt => attempt.ConnectTask));
            Task fallbackDelayTask = Task.Delay(fallbackDelay, cancellationToken);
            Task completedTask = await Task.WhenAny(completedConnectTask, fallbackDelayTask).ConfigureAwait(false);
            if (completedTask == fallbackDelayTask)
            {
                await fallbackDelayTask.ConfigureAwait(false);
                return null;
            }

            Task completedConnect = await completedConnectTask.ConfigureAwait(false);
            return RemoveCompletedAttempt(attempts, completedConnect);
        }

        private static ConnectAttempt RemoveCompletedAttempt(List<ConnectAttempt> attempts, Task completedTask)
        {
            ConnectAttempt completedAttempt = attempts.Single(attempt => ReferenceEquals(attempt.ConnectTask, completedTask));
            attempts.Remove(completedAttempt);
            return completedAttempt;
        }

        internal static void ObserveConnectCleanupFailure(Task connectTask)
        {
            ArgumentNullException.ThrowIfNull(connectTask);
            if (connectTask.IsFaulted)
            {
                _ = connectTask.Exception;
                return;
            }

            if (connectTask.IsCompleted)
            {
                return;
            }

            _ = connectTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        internal class ConnectAttempt : IDisposable
        {
            private bool _completed;

            public ConnectAttempt(IPAddress address, Socket socket, Task connectTask)
            {
                Address = address;
                Socket = socket;
                ConnectTask = connectTask;
            }

            public IPAddress Address { get; }

            public Socket Socket { get; }

            public Task ConnectTask { get; }

            public NetworkStream CreateStream()
            {
                _completed = true;
                return new NetworkStream(Socket, ownsSocket: true);
            }

            public void Dispose()
            {
                if (!_completed)
                {
                    Socket.Dispose();
                    ObserveConnectCleanupFailure(ConnectTask);
                }
            }
        }
    }
}
