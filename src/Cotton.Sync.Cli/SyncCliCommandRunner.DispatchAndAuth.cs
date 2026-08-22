// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.Auth;
using Cotton.Sync.State;
using System.Net;

namespace Cotton.Sync.Cli
{
    public static partial class SyncCliCommandRunner
    {

        /// <summary>
        /// Runs a CLI command and returns the process exit code.
        /// </summary>
        public static async Task<int> RunAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            return await RunAsync(args, output, error, null, cancellationToken)
                .ConfigureAwait(false);
        }


        internal static async Task<int> RunAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? httpClient,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            if (args.Count == 0 || IsHelp(args[0]))
            {
                await WriteHelpAsync(output).ConfigureAwait(false);
                return 0;
            }

            string command = args[0];
            if (IsVersion(command))
            {
                await output.WriteLineAsync(SyncCliAppVersion.Current).ConfigureAwait(false);
                return 0;
            }

            if (string.Equals(command, AuthBrowserCommand, StringComparison.OrdinalIgnoreCase))
            {
                return await RunAuthBrowserAsync(args.Skip(1).ToArray(), output, error, httpClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(command, StateSummaryCommand, StringComparison.OrdinalIgnoreCase))
            {
                return await RunStateSummaryAsync(args.Skip(1).ToArray(), output, error, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(command, SyncOnceCommand, StringComparison.OrdinalIgnoreCase))
            {
                return await RunSyncOnceAsync(args.Skip(1).ToArray(), output, error, httpClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(command, SyncSoakCommand, StringComparison.OrdinalIgnoreCase))
            {
                return await SyncCliSoakCommandRunner
                    .RunAsync(args.Skip(1).ToArray(), output, error, httpClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(command, SyncCrudSmokeCommand, StringComparison.OrdinalIgnoreCase))
            {
                return await SyncCliCrudSmokeCommandRunner
                    .RunAsync(args.Skip(1).ToArray(), output, error, httpClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            await error.WriteLineAsync("Unknown command: " + command).ConfigureAwait(false);
            await WriteHelpAsync(error).ConfigureAwait(false);
            return 2;
        }


        private static async Task<int> RunAuthBrowserAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? injectedHttpClient,
            CancellationToken cancellationToken)
        {
            SyncCliBrowserAuthOptions? options = SyncCliOptionsReader.ReadBrowserAuthOptions(args, error);
            if (options is null)
            {
                return 2;
            }

            using HttpClient? ownedHttpClient = injectedHttpClient is null ? new HttpClient() : null;
            HttpClient httpClient = injectedHttpClient ?? ownedHttpClient!;
            await using CottonCloudClient client = new(
                httpClient,
                new InMemoryCottonTokenStore(),
                new CottonSdkOptions
                {
                    BaseAddress = options.ServerUri,
                    RefreshOnUnauthorized = false,
                    UserAgent = "CottonSyncCli",
                    DeviceName = options.DeviceName,
                });
            await output.WriteLineAsync("Cotton Sync browser sign-in").ConfigureAwait(false);
            return await RunBrowserSignInAsync(options, client, output, error, cancellationToken).ConfigureAwait(false);
        }


        private static async Task<int> RunBrowserSignInAsync(
            SyncCliBrowserAuthOptions options,
            CottonCloudClient client,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            AppCodeBrowserAuthFlow authFlow = new(
                client.Auth,
                new SyncCliApprovalUrlWriter(output));
            using CancellationTokenSource? timeoutCancellation = options.TimeoutSeconds.HasValue
                ? new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds.Value))
                : null;
            using CancellationTokenSource? linkedCancellation = timeoutCancellation is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            CancellationToken signInCancellation = linkedCancellation?.Token ?? cancellationToken;
            try
            {
                AuthSession session = await authFlow
                    .SignInAsync(
                        new AppCodeBrowserSignInRequest
                        {
                            ApplicationName = options.ApplicationName,
                            ApplicationVersion = options.ApplicationVersion,
                            DeviceName = options.DeviceName,
                        },
                        signInCancellation)
                    .ConfigureAwait(false);
                string account = string.IsNullOrWhiteSpace(session.Email) ? session.Username : session.Email!;
                await output.WriteLineAsync("Signed in: " + account).ConfigureAwait(false);
                await client.Auth.LogoutAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync("Signed out.").ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (timeoutCancellation?.IsCancellationRequested == true
                && !cancellationToken.IsCancellationRequested)
            {
                await error.WriteLineAsync("Browser sign-in timed out before approval completed.").ConfigureAwait(false);
                return 1;
            }
            catch (AppCodeBrowserSignInException exception)
            {
                await SyncCliErrorWriter.WriteBrowserSignInAsync(error, exception).ConfigureAwait(false);
                return 1;
            }
        }


        private static async Task<int> RunStateSummaryAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            string? databasePath = SyncCliOptionsReader.ReadOption(args, "--database");
            string? syncPairId = SyncCliOptionsReader.ReadOption(args, "--sync-pair");
            if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(syncPairId))
            {
                await error.WriteLineAsync("state-summary requires --database and --sync-pair.").ConfigureAwait(false);
                return 2;
            }

            IReadOnlyList<SyncStateEntry> entries;
            SyncChangeCursor cursor;
            try
            {
                SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                entries = await store
                    .LoadPairAsync(syncPairId, cancellationToken)
                    .ConfigureAwait(false);
                cursor = await store
                    .GetChangeCursorAsync(syncPairId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStateDatabaseReadException(exception))
            {
                await error
                    .WriteLineAsync(
                        "state-summary could not read the sync-state database. The file may be corrupt or not a Cotton Sync state database: "
                        + exception.Message)
                    .ConfigureAwait(false);
                return 2;
            }

            await output.WriteLineAsync("Cotton Sync state summary").ConfigureAwait(false);
            await output.WriteLineAsync("Database: " + databasePath).ConfigureAwait(false);
            await output.WriteLineAsync("Sync pair: " + syncPairId).ConfigureAwait(false);
            await output.WriteLineAsync("Entries: " + entries.Count.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Remote cursor: " + cursor.LastCursor.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Cursor updated UTC: " + SyncCliFormat.FormatUtc(cursor.UpdatedAtUtc)).ConfigureAwait(false);
            return 0;
        }


        private static bool IsStateDatabaseReadException(Exception exception)
        {
            if (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                string? typeName = current.GetType().FullName;
                if (string.Equals(typeName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal)
                    || string.Equals(typeName, "Microsoft.EntityFrameworkCore.DbUpdateException", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
