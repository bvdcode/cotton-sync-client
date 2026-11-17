// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class LinuxTrayHostProbe
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        public static async Task<bool> IsAvailableAsync()
        {
            string? address = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            using DBusConnection connection = new(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(ProbeTimeout).ConfigureAwait(false);
                return await connection.CallMethodAsync(
                    CreateRequest(connection),
                    static (Message message, object? state) =>
                    {
                        Reader reader = message.GetBodyReader();
                        reader.ReadSignature();
                        return reader.ReadBool();
                    },
                    null).WaitAsync(ProbeTimeout).ConfigureAwait(false);
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
