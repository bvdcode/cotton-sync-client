// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Cotton.Sync.Desktop.Updates
{
    internal static class LinuxPackageKitInstaller
    {
        private const string Service = "org.freedesktop.PackageKit";
        private const string ServicePath = "/org/freedesktop/PackageKit";
        private const string TransactionInterface = Service + ".Transaction";
        private const uint SuccessfulExit = 1;
        private const ulong InstallLocalPackageFlags = 0;

        public static async Task<int> InstallAsync(string packagePath, CancellationToken cancellationToken)
        {
            string? address = DBusAddress.System;
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException("The system D-Bus address is unavailable.");
            }

            using DBusConnection connection = new(address);
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            string transaction = await connection.CallMethodAsync(
                CreateTransactionRequest(connection),
                static (Message message, object? state) => message.GetBodyReader().ReadObjectPathAsString(),
                null).WaitAsync(cancellationToken).ConfigureAwait(false);
            TaskCompletionSource<uint> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string? failureDetails = null;
            using IDisposable finished = await connection.AddMatchAsync(
                CreateSignalRule(transaction, "Finished"),
                static (Message message, object? state) => message.GetBodyReader().ReadUInt32(),
                (Notification<uint> notification) =>
                {
                    if (notification.IsCompletion)
                    {
                        completion.TrySetException(notification.Exception);
                    }
                    else if (notification.HasValue)
                    {
                        completion.TrySetResult(notification.Value);
                    }
                },
                emitOnCapturedContext: false,
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed).ConfigureAwait(false);
            using IDisposable errors = await connection.AddMatchAsync(
                CreateSignalRule(transaction, "ErrorCode"),
                ReadError,
                (Notification<string> notification) =>
                {
                    if (notification.IsCompletion)
                    {
                        completion.TrySetException(notification.Exception);
                    }
                    else if (notification.HasValue)
                    {
                        failureDetails = notification.Value;
                    }
                },
                emitOnCapturedContext: false,
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed).ConfigureAwait(false);
            await connection.CallMethodAsync(CreateHintsRequest(connection, transaction))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            await connection.CallMethodAsync(CreateInstallRequest(connection, transaction, packagePath))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            uint exitCode = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (exitCode != SuccessfulExit)
            {
                throw new InvalidOperationException("The system package manager could not install Cotton Sync: "
                    + (failureDetails ?? "transaction exit " + exitCode));
            }

            Trace.TraceInformation("Linux package update completed through PackageKit.");
            uint processId = await connection.CallMethodAsync(
                CreateProcessIdRequest(connection),
                static (Message message, object? state) => message.GetBodyReader().ReadUInt32(),
                null).WaitAsync(cancellationToken).ConfigureAwait(false);
            return checked((int)processId);
        }

        private static MatchRule CreateSignalRule(string transaction, string member) => new()
        {
            Type = MessageType.Signal,
            Sender = Service,
            Path = transaction,
            Interface = TransactionInterface,
            Member = member,
        };

        private static string ReadError(Message message, object? state)
        {
            Reader reader = message.GetBodyReader();
            uint code = reader.ReadUInt32();
            return code + ": " + reader.ReadString();
        }

        private static MessageBuffer CreateTransactionRequest(DBusConnection connection)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: ServicePath,
                @interface: Service, member: "CreateTransaction");
            return writer.CreateMessage();
        }

        private static MessageBuffer CreateHintsRequest(DBusConnection connection, string transaction)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: transaction,
                @interface: TransactionInterface, member: "SetHints", signature: "as");
            writer.WriteArray(new[] { "interactive=true" });
            return writer.CreateMessage();
        }

        private static MessageBuffer CreateInstallRequest(DBusConnection connection, string transaction, string packagePath)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: transaction,
                @interface: TransactionInterface, member: "InstallFiles", signature: "tas",
                flags: MessageFlags.AllowInteractiveAuthorization);
            writer.WriteUInt64(InstallLocalPackageFlags);
            writer.WriteArray(new[] { Path.GetFullPath(packagePath) });
            return writer.CreateMessage();
        }

        private static MessageBuffer CreateProcessIdRequest(DBusConnection connection)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus", member: "GetConnectionUnixProcessID", signature: "s");
            writer.WriteString(Service);
            return writer.CreateMessage();
        }
    }
}
