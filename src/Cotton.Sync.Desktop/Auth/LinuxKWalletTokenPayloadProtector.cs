// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tmds.DBus.Protocol;

namespace Cotton.Sync.Desktop.Auth
{
    internal class LinuxKWalletTokenPayloadProtector : ITokenPayloadProtector, IDeletableTokenPayloadProtector
    {
        private const string Interface = "org.kde.KWallet";
        private const string Application = "Cotton Sync";
        private const string Folder = "Cotton Sync";
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
        private readonly string _service;
        private readonly string _path;

        public LinuxKWalletTokenPayloadProtector(int version)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(version, 5);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 6);
            _service = "org.kde.kwalletd" + version;
            _path = "/modules/kwalletd" + version;
            Scheme = "linux-kwallet" + version + "-v1";
        }

        public string Scheme { get; }

        public async Task<byte[]> ProtectAsync(byte[] plaintext, CancellationToken cancellationToken = default)
        {
            using DBusConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            string wallet = await connection.CallMethodAsync(CreateWalletRequest(connection, null),
                static (Message message, object? state) => message.GetBodyReader().ReadString(), null)
                .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
            string[] wallets = await connection.CallMethodAsync(CreateWalletListRequest(connection),
                static (Message message, object? state) => message.GetBodyReader().ReadArrayOfString(), null)
                .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (!wallets.Contains(wallet, StringComparer.Ordinal))
            {
                throw new CryptographicException("No existing local KWallet is configured on this desktop.");
            }

            KWalletTokenReference reference = new(wallet, Guid.NewGuid().ToString("N"));
            await ExecuteAsync(connection, reference, KWalletOperation.Write, Convert.ToBase64String(plaintext), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(reference);
        }

        public async Task<byte[]> UnprotectAsync(byte[] protectedPayload, CancellationToken cancellationToken = default)
        {
            KWalletTokenReference reference = Decode(protectedPayload);
            using DBusConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            string secret = await ExecuteAsync(connection, reference, KWalletOperation.Read, null, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new CryptographicException("The saved token entry is unavailable in KWallet.");
            }

            try
            {
                return Convert.FromBase64String(secret);
            }
            catch (FormatException exception)
            {
                throw new CryptographicException("KWallet returned an invalid token payload.", exception);
            }
        }

        public async Task DeleteAsync(byte[] protectedPayload, CancellationToken cancellationToken = default)
        {
            KWalletTokenReference reference = Decode(protectedPayload);
            using DBusConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, reference, KWalletOperation.Delete, null, cancellationToken).ConfigureAwait(false);
        }

        private static KWalletTokenReference Decode(byte[] payload)
        {
            KWalletTokenReference reference = JsonSerializer.Deserialize<KWalletTokenReference>(payload)
                ?? throw new CryptographicException("The KWallet token reference is missing.");
            if (string.IsNullOrWhiteSpace(reference.Wallet) || !Guid.TryParseExact(reference.Entry, "N", out _))
            {
                throw new CryptographicException("The KWallet token reference is invalid.");
            }

            return reference;
        }

        private static async Task<DBusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            string address = DBusAddress.Session
                ?? throw new CryptographicException("KWallet is unavailable without a desktop D-Bus session.");
            DBusConnection connection = new(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private async Task<string> ExecuteAsync(DBusConnection connection, KWalletTokenReference reference,
            KWalletOperation operation, string? secret, CancellationToken cancellationToken)
        {
            int handle = await connection.CallMethodAsync(CreateWalletRequest(connection, reference.Wallet),
                static (Message message, object? state) => message.GetBodyReader().ReadInt32(), null)
                .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (handle < 0)
            {
                throw new CryptographicException("KWallet could not open the selected wallet.");
            }

            try
            {
                if (operation == KWalletOperation.Write)
                {
                    bool created = await connection.CallMethodAsync(
                        CreateEntryRequest(connection, handle, reference.Entry, KWalletOperation.CreateFolder, null),
                        static (Message message, object? state) => message.GetBodyReader().ReadBool(), null)
                        .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
                    if (!created)
                    {
                        throw new CryptographicException("KWallet could not open the Cotton Sync folder.");
                    }
                }

                if (operation == KWalletOperation.Read)
                {
                    return await connection.CallMethodAsync(CreateEntryRequest(connection, handle, reference.Entry, operation, null),
                        static (Message message, object? state) => message.GetBodyReader().ReadString(), null)
                        .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
                }

                int result = await connection.CallMethodAsync(CreateEntryRequest(connection, handle, reference.Entry, operation, secret),
                    static (Message message, object? state) => message.GetBodyReader().ReadInt32(), null)
                    .WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
                if (result != 0)
                {
                    throw new CryptographicException("KWallet could not " + operation.ToString().ToLowerInvariant() + " the token entry.");
                }

                return string.Empty;
            }
            finally
            {
                try
                {
                    await connection.CallMethodAsync(CreateEntryRequest(connection, handle, reference.Entry, KWalletOperation.Close, null),
                        static (Message message, object? state) => message.GetBodyReader().ReadInt32(), null)
                        .WaitAsync(OperationTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is DBusErrorReplyException or DBusConnectionClosedException or TimeoutException)
                {
                    Trace.TraceWarning("KWallet token handle cleanup failed: {0}", exception.Message);
                }
            }
        }

        private MessageBuffer CreateWalletListRequest(DBusConnection connection)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: _service, path: _path, @interface: Interface, member: "wallets");
            return writer.CreateMessage();
        }

        private MessageBuffer CreateWalletRequest(DBusConnection connection, string? wallet)
        {
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: _service, path: _path, @interface: Interface,
                member: wallet is null ? "localWallet" : "open", signature: wallet is null ? null : "sxs");
            if (wallet is not null)
            {
                writer.WriteString(wallet);
                writer.WriteInt64(0);
                writer.WriteString(Application);
            }

            return writer.CreateMessage();
        }

        private MessageBuffer CreateEntryRequest(DBusConnection connection, int handle, string entry,
            KWalletOperation operation, string? secret)
        {
            (string member, string signature) = operation switch
            {
                KWalletOperation.Read => ("readPassword", "isss"),
                KWalletOperation.Write => ("writePassword", "issss"),
                KWalletOperation.Delete => ("removeEntry", "isss"),
                KWalletOperation.CreateFolder => ("createFolder", "iss"),
                KWalletOperation.Close => ("close", "ibs"),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: _service, path: _path, @interface: Interface,
                member: member, signature: signature);
            writer.WriteInt32(handle);
            if (operation == KWalletOperation.Close)
            {
                writer.WriteBool(false);
            }
            else
            {
                writer.WriteString(Folder);
                if (operation != KWalletOperation.CreateFolder)
                {
                    writer.WriteString(entry);
                }

                if (operation == KWalletOperation.Write)
                {
                    writer.WriteString(secret!);
                }
            }

            writer.WriteString(Application);
            return writer.CreateMessage();
        }
    }
}
