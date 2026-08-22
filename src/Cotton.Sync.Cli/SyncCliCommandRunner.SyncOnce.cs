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

        private static async Task<int> RunSyncOnceAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? injectedHttpClient,
            CancellationToken cancellationToken)
        {
            SyncCliConnectionOptions? options = SyncCliOptionsReader.ReadConnectionOptions(
                args,
                error,
                SyncOnceCommand,
                allowBrowserLogin: true);
            if (options is null)
            {
                return 2;
            }

            using HttpClient? ownedHttpClient = injectedHttpClient is null ? new HttpClient() : null;
            HttpClient httpClient = injectedHttpClient ?? ownedHttpClient!;
            try
            {
                SyncCliPassResult pass = await RunSyncOnceWithRetryAsync(options, output, httpClient, cancellationToken)
                    .ConfigureAwait(false);
                await WriteSyncOnceSuccessAsync(output, options, pass).ConfigureAwait(false);
                return 0;
            }
            catch (AppCodeBrowserSignInException exception)
            {
                await SyncCliErrorWriter.WriteBrowserSignInAsync(error, exception).ConfigureAwait(false);
                return 1;
            }
            catch (Exception exception) when (IsSupportableSyncOnceException(exception))
            {
                await WriteSyncOnceFailureAsync(error, options, exception).ConfigureAwait(false);
                return 1;
            }
        }


        private static async Task<SyncCliPassResult> RunSyncOnceWithRetryAsync(
            SyncCliConnectionOptions options,
            TextWriter output,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            await using SyncCliRuntime runtime = await CreateSyncCliRuntimeWithRetryAsync(
                    options,
                    output,
                    httpClient,
                    cancellationToken)
                .ConfigureAwait(false);
            if (options.UseBrowserLogin)
            {
                await output.WriteLineAsync("Browser approval completed. Starting sync...").ConfigureAwait(false);
            }

            return await RunSyncOncePassWithRetryAsync(runtime, output, cancellationToken).ConfigureAwait(false);
        }


        private static async Task<SyncCliRuntime> CreateSyncCliRuntimeWithRetryAsync(
            SyncCliConnectionOptions options,
            TextWriter output,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= SyncOnceMaxTransientAttempts; attempt++)
            {
                try
                {
                    return options.UseBrowserLogin
                        ? await SyncCliRuntimeFactory
                            .CreateWithBrowserAuthAsync(
                                options,
                                httpClient,
                                new SyncCliApprovalUrlWriter(output),
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await SyncCliRuntimeFactory.CreateAsync(options, httpClient, cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (AppCodeBrowserSignInException exception)
                    when (IsRetriableBrowserSignInException(exception) && attempt < SyncOnceMaxTransientAttempts)
                {
                    await WriteSyncOnceRetryAsync(output, exception, attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (AppCodeBrowserSignInException)
                {
                    throw;
                }
                catch (Exception exception) when (IsRetriableSyncOnceException(exception) && attempt < SyncOnceMaxTransientAttempts)
                {
                    await WriteSyncOnceRetryAsync(output, exception, attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("sync-once runtime retry attempts were exhausted.");
        }


        private static async Task<SyncCliPassResult> RunSyncOncePassWithRetryAsync(
            SyncCliRuntime runtime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= SyncOnceMaxTransientAttempts; attempt++)
            {
                try
                {
                    return await SyncCliRuntimeFactory
                        .RunSinglePassAsync(
                            runtime,
                            new SyncRunOptions { RunProgress = new SyncCliRunProgressWriter(output) },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRetriableSyncOnceException(exception) && attempt < SyncOnceMaxTransientAttempts)
                {
                    await WriteSyncOnceRetryAsync(output, exception, attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("sync-once pass retry attempts were exhausted.");
        }


        private static async Task WriteSyncOnceRetryAsync(
            TextWriter output,
            Exception exception,
            int completedAttempts,
            CancellationToken cancellationToken)
        {
            TimeSpan delay = GetSyncOnceRetryDelay(completedAttempts);
            await output
                .WriteLineAsync(
                    "Transient sync failure: "
                    + FormatSyncOnceFailure(exception)
                    + " Retrying attempt "
                    + (completedAttempts + 1).ToStringInvariant()
                    + " of "
                    + SyncOnceMaxTransientAttempts.ToStringInvariant()
                    + " after "
                    + FormatRetryDelay(delay)
                    + ".")
                .ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }


        internal static async Task WriteSyncOnceSuccessAsync(
            TextWriter output,
            SyncCliConnectionOptions options,
            SyncCliPassResult pass)
        {
            await output.WriteLineAsync("Cotton Sync one-shot run").ConfigureAwait(false);
            await output.WriteLineAsync("Sync pair: " + options.SyncPairId).ConfigureAwait(false);
            await output.WriteLineAsync("Activities: " + pass.Result.TotalActivityCount.ToStringInvariant()).ConfigureAwait(false);
            if (pass.Result.IsActivityListTruncated)
            {
                await output.WriteLineAsync("Retained activities: " + pass.Result.Activities.Count.ToStringInvariant()).ConfigureAwait(false);
            }

            foreach (SyncActivity activity in pass.Result.Activities)
            {
                await output
                    .WriteLineAsync(activity.Kind + " " + activity.RelativePath + SyncCliFormat.FormatActivityDetails(activity.Details))
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync("State entries: " + pass.StateEntries.Count.ToStringInvariant()).ConfigureAwait(false);
        }
    }
}
