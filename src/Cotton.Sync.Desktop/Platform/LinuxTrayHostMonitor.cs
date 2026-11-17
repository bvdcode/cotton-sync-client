// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class LinuxTrayHostMonitor
    {
        public static async Task RunAsync(Action<bool> reportAvailability, CancellationToken cancellationToken)
        {
            string? address = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
            try
            {
                using DBusConnection connection = new(address);
                await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                using IDisposable ownerChanges = await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = "org.freedesktop.DBus",
                        Interface = "org.freedesktop.DBus",
                        Member = "NameOwnerChanged",
                        Arg0 = "org.kde.StatusNotifierWatcher",
                    },
                    (Notification notification) => { changes.Writer.TryWrite(true); }).ConfigureAwait(false);
                using IDisposable hostChanges = await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = "org.kde.StatusNotifierWatcher",
                        Interface = "org.kde.StatusNotifierWatcher",
                    },
                    (Notification notification) => { changes.Writer.TryWrite(true); }).ConfigureAwait(false);
                changes.Writer.TryWrite(true);
                await foreach (bool change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    reportAvailability(await LinuxTrayHostProbe.IsAvailableAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Linux tray availability monitoring stopped: {0}", exception);
            }
        }
    }
}
