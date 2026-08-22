// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync;
using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliSoakCommandRunner
    {

        public static async Task<int> RunAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? injectedHttpClient,
            CancellationToken cancellationToken)
        {
            SyncCliSoakSettings? settings = await ReadSettingsAsync(args, error).ConfigureAwait(false);
            if (settings is null)
            {
                return 2;
            }

            using HttpClient? ownedHttpClient = injectedHttpClient is null ? new HttpClient() : null;
            HttpClient httpClient = injectedHttpClient ?? ownedHttpClient!;
            try
            {
                return await RunWithRuntimesAsync(settings, httpClient, output, cancellationToken).ConfigureAwait(false);
            }
            catch (AppCodeBrowserSignInException exception)
            {
                await SyncCliErrorWriter.WriteBrowserSignInAsync(error, exception).ConfigureAwait(false);
                return 1;
            }
        }


        private static async Task<SyncCliSoakSettings?> ReadSettingsAsync(
            IReadOnlyList<string> args,
            TextWriter error)
        {
            SyncCliConnectionOptions? options = SyncCliOptionsReader.ReadConnectionOptions(
                args,
                error,
                "sync-soak",
                allowBrowserLogin: true);
            if (options is null
                || !TryReadRunLimits(args, error, out int? iterations, out int? durationSeconds, out int intervalSeconds))
            {
                return null;
            }

            if (!TryReadProbeFile(args, options.LocalRoot, out string? normalizedProbeFile, out string probeError))
            {
                await error.WriteLineAsync(probeError).ConfigureAwait(false);
                return null;
            }

            SyncCliConnectionOptions? secondClientOptions = ReadSecondClientOptions(args, options, error);
            if (HasSecondClientOption(args) && secondClientOptions is null)
            {
                return null;
            }

            return new SyncCliSoakSettings(
                options,
                secondClientOptions,
                iterations,
                durationSeconds,
                intervalSeconds,
                normalizedProbeFile);
        }


        private static bool TryReadRunLimits(
            IReadOnlyList<string> args,
            TextWriter error,
            out int? iterations,
            out int? durationSeconds,
            out int intervalSeconds)
        {
            iterations = null;
            durationSeconds = null;
            intervalSeconds = 30;
            if (!SyncCliOptionsReader.TryReadOptionalPositiveInt(args, "--iterations", error, out iterations)
                || !SyncCliOptionsReader.TryReadOptionalPositiveInt(args, "--duration-seconds", error, out durationSeconds)
                || !SyncCliOptionsReader.TryReadOptionalPositiveInt(args, "--interval-seconds", error, out int? parsedInterval))
            {
                return false;
            }

            if (!iterations.HasValue && !durationSeconds.HasValue)
            {
                error.WriteLine("sync-soak requires --iterations or --duration-seconds.");
                return false;
            }

            intervalSeconds = parsedInterval ?? intervalSeconds;
            return true;
        }


        private static bool TryReadProbeFile(
            IReadOnlyList<string> args,
            string localRoot,
            out string? normalizedProbeFile,
            out string error)
        {
            string? probeFile = SyncCliOptionsReader.ReadOption(args, "--probe-file");
            if (string.IsNullOrWhiteSpace(probeFile))
            {
                normalizedProbeFile = null;
                error = string.Empty;
                return true;
            }

            return SyncCliOptionsReader.TryNormalizeProbeFile(
                localRoot,
                probeFile,
                out normalizedProbeFile,
                out error);
        }


        private static async Task<int> RunWithRuntimesAsync(
            SyncCliSoakSettings settings,
            HttpClient httpClient,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await using SyncCliRuntime runtime = await CreateRuntimeAsync(
                settings.ConnectionOptions,
                httpClient,
                output,
                cancellationToken).ConfigureAwait(false);
            if (settings.SecondConnectionOptions is null)
            {
                return await RunLoopAsync(settings, runtime, secondRuntime: null, output, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using SyncCliRuntime secondRuntime = await CreateRuntimeAsync(
                settings.SecondConnectionOptions,
                httpClient,
                output,
                cancellationToken).ConfigureAwait(false);
            return await RunLoopAsync(settings, runtime, secondRuntime, output, cancellationToken).ConfigureAwait(false);
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
