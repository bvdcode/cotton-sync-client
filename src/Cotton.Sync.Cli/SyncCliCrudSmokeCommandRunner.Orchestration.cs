// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliCrudSmokeCommandRunner
    {

        public static async Task<int> RunAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? injectedHttpClient,
            CancellationToken cancellationToken)
        {
            SyncCliConnectionOptions? firstOptions = SyncCliOptionsReader.ReadConnectionOptions(
                args,
                error,
                "sync-crud-smoke",
                allowBrowserLogin: true);
            if (firstOptions is null)
            {
                return 2;
            }

            SyncCliConnectionOptions? secondOptions = ReadSecondClientOptions(args, firstOptions, error);
            if (secondOptions is null)
            {
                return 2;
            }

            string? localRootError = ValidateLocalRoots(firstOptions.LocalRoot, secondOptions.LocalRoot);
            if (localRootError is not null)
            {
                await error.WriteLineAsync(localRootError).ConfigureAwait(false);
                return 2;
            }

            using HttpClient? ownedHttpClient = injectedHttpClient is null ? new HttpClient() : null;
            HttpClient httpClient = injectedHttpClient ?? ownedHttpClient!;
            return await RunWithErrorHandlingAsync(
                firstOptions,
                secondOptions,
                httpClient,
                output,
                error,
                cancellationToken).ConfigureAwait(false);
        }


        private static async Task<int> RunWithErrorHandlingAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            HttpClient httpClient,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            try
            {
                return await RunSmokeAsync(firstOptions, secondOptions, httpClient, output, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AppCodeBrowserSignInException exception)
            {
                await SyncCliErrorWriter.WriteBrowserSignInAsync(error, exception).ConfigureAwait(false);
                return 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await SyncCliErrorWriter.WriteCommandFailureAsync(error, "sync-crud-smoke failed.", exception)
                    .ConfigureAwait(false);
                return 1;
            }
        }


        private static async Task<int> RunSmokeAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            HttpClient httpClient,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(firstOptions.LocalRoot);
            Directory.CreateDirectory(secondOptions.LocalRoot);
            await using SyncCliRuntime firstRuntime = await CreateRuntimeAsync(
                firstOptions,
                httpClient,
                output,
                cancellationToken).ConfigureAwait(false);
            await using SyncCliRuntime secondRuntime = await CreateRuntimeAsync(
                secondOptions,
                httpClient,
                output,
                cancellationToken).ConfigureAwait(false);

            await WriteHeaderAsync(firstOptions, secondOptions, output).ConfigureAwait(false);
            int failures = await RunScenariosAsync(
                    firstOptions,
                    secondOptions,
                    firstRuntime,
                    secondRuntime,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            return await WriteFinalResultAsync(firstRuntime, secondRuntime, output, failures, cancellationToken)
                .ConfigureAwait(false);
        }


        private static async Task WriteHeaderAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            TextWriter output)
        {
            await output.WriteLineAsync("Cotton Sync CRUD smoke").ConfigureAwait(false);
            await output.WriteLineAsync("Sync pair: " + firstOptions.SyncPairId).ConfigureAwait(false);
            await output.WriteLineAsync("Second sync pair: " + secondOptions.SyncPairId).ConfigureAwait(false);
            await output.WriteLineAsync("Remote root: " + FormatRemoteRoot(firstOptions)).ConfigureAwait(false);
            await output.WriteLineAsync("Local root: " + firstOptions.LocalRoot).ConfigureAwait(false);
            await output.WriteLineAsync("Second local root: " + secondOptions.LocalRoot).ConfigureAwait(false);
        }


        private static async Task<int> RunScenariosAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            failures += await RunInitialConvergenceAsync(firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientACreateAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientBCreateAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientARenameAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientBRenameAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientADeleteAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            failures += await RunClientBDeleteAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                .ConfigureAwait(false);
            return failures;
        }


        private static async Task<int> WriteFinalResultAsync(
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            int failures,
            CancellationToken cancellationToken)
        {
            SyncCliConvergenceResult finalFirst = await RunConvergenceAsync(firstRuntime, cancellationToken)
                .ConfigureAwait(false);
            SyncCliConvergenceResult finalSecond = await RunConvergenceAsync(secondRuntime, cancellationToken)
                .ConfigureAwait(false);
            int finalActivities = GetActivityCount(finalFirst.Pass) + GetActivityCount(finalSecond.Pass);
            int finalStateEntries = finalFirst.Pass.StateEntries.Count + finalSecond.Pass.StateEntries.Count;
            if (!finalFirst.Converged || !finalSecond.Converged)
            {
                failures++;
            }

            await output.WriteLineAsync("Final convergence activities: " + finalActivities.ToStringInvariant())
                .ConfigureAwait(false);
            await output.WriteLineAsync("Final state entries: " + finalStateEntries.ToStringInvariant())
                .ConfigureAwait(false);
            await output.WriteLineAsync("Converged: " + (failures == 0 ? "yes" : "no")).ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToStringInvariant()).ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }


        private static Task<SyncCliRuntime> CreateRuntimeAsync(
            SyncCliConnectionOptions options,
            HttpClient httpClient,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            return options.UseBrowserLogin
                ? SyncCliRuntimeFactory.CreateWithBrowserAuthAsync(
                    options,
                    httpClient,
                    new SyncCliApprovalUrlWriter(output),
                    cancellationToken)
                : SyncCliRuntimeFactory.CreateAsync(options, httpClient, cancellationToken);
        }
    }
}
