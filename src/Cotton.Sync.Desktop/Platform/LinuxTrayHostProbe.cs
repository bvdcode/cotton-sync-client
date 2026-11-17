// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class LinuxTrayHostProbe
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ObjectReadyInterval = TimeSpan.FromMilliseconds(100);

        public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            string? address = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            using DBusConnection connection = new(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
                Stopwatch readiness = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        return await connection.CallMethodAsync(
                            CreateRequest(connection),
                            static (Message message, object? state) =>
                            {
                                Reader reader = message.GetBodyReader();
                                reader.ReadSignature();
                                return reader.ReadBool();
                            },
                            null).WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DBusErrorReplyException exception) when (
                        exception.ErrorName is "org.freedesktop.DBus.Error.UnknownObject"
                            or "org.freedesktop.DBus.Error.UnknownMethod"
                        && readiness.Elapsed < ProbeTimeout)
                    {
                        await Task.Delay(ObjectReadyInterval, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is DBusErrorReplyException or IOException or TimeoutException
                or System.Net.Sockets.SocketException or DBusConnectFailedException)
            {
                Trace.TraceInformation("Linux tray host is unavailable: {0}", exception.Message);
                return false;
            }
        }

        private static MessageBuffer CreateRequest(DBusConnection connection)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.kde.StatusNotifierWatcher",
                path: "/StatusNotifierWatcher",
                @interface: "org.freedesktop.DBus.Properties",
                member: "Get",
                signature: "ss");
            writer.WriteString("org.kde.StatusNotifierWatcher");
            writer.WriteString("IsStatusNotifierHostRegistered");
            return writer.CreateMessage();
        }
    }
}
