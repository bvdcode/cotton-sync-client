// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync;
using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliSoakCommandRunner
    {

        private static async Task<int> RunLoopAsync(
            SyncCliSoakSettings settings,
            SyncCliRuntime runtime,
            SyncCliRuntime? secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            using SyncCliSoakStatistics statistics = new(settings.DurationSeconds);
            await WriteRunHeaderAsync(settings, statistics, output).ConfigureAwait(false);
            try
            {
                await RunIterationsAsync(settings, runtime, secondRuntime, statistics, output, cancellationToken)
                    .ConfigureAwait(false);
                await CaptureFinalConvergenceAsync(runtime, secondRuntime, statistics, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                statistics.RecordError();
                await output.WriteLineAsync("Sync error: " + FormatException(exception)).ConfigureAwait(false);
            }

            await WriteSummaryAsync(output, statistics).ConfigureAwait(false);
            return statistics.IsConverged ? 0 : 1;
        }


        private static async Task WriteRunHeaderAsync(
            SyncCliSoakSettings settings,
            SyncCliSoakStatistics statistics,
            TextWriter output)
        {
            await output.WriteLineAsync("Cotton Sync soak run").ConfigureAwait(false);
            await output.WriteLineAsync("Sync pair: " + settings.ConnectionOptions.SyncPairId).ConfigureAwait(false);
            if (settings.SecondConnectionOptions is not null)
            {
                await output.WriteLineAsync("Second sync pair: " + settings.SecondConnectionOptions.SyncPairId)
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync("Started UTC: " + SyncCliFormat.FormatUtc(statistics.StartedAt))
                .ConfigureAwait(false);
        }


        private static async Task RunIterationsAsync(
            SyncCliSoakSettings settings,
            SyncCliRuntime runtime,
            SyncCliRuntime? secondRuntime,
            SyncCliSoakStatistics statistics,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            while (ShouldRunNextSoakIteration(statistics.CompletedIterations, settings.Iterations, statistics.StopAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int iteration = statistics.CompletedIterations + 1;
                long iterationStartedTimestamp = Stopwatch.GetTimestamp();
                await WriteProbeFileIfConfiguredAsync(settings, iteration, cancellationToken).ConfigureAwait(false);
                SyncCliPassResult pass = await RunSoakPassAsync(runtime, cancellationToken).ConfigureAwait(false);
                SyncCliPassResult? secondPass = await RunOptionalSoakPassAsync(secondRuntime, cancellationToken)
                    .ConfigureAwait(false);
                TimeSpan iterationElapsed = Stopwatch.GetElapsedTime(iterationStartedTimestamp);
                statistics.RecordIteration(pass, secondPass, iterationElapsed);
                await WriteIterationAsync(output, iteration, pass, secondPass, statistics, iterationElapsed)
                    .ConfigureAwait(false);

                if (!ShouldRunNextSoakIteration(statistics.CompletedIterations, settings.Iterations, statistics.StopAt))
                {
                    break;
                }

                await Task.Delay(GetNextSoakDelay(settings.IntervalSeconds, statistics.StopAt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }


        private static async Task WriteProbeFileIfConfiguredAsync(
            SyncCliSoakSettings settings,
            int iteration,
            CancellationToken cancellationToken)
        {
            if (settings.ProbeFile is null)
            {
                return;
            }

            await WriteProbeFileAsync(
                    settings.ConnectionOptions.LocalRoot,
                    settings.ProbeFile,
                    iteration,
                    cancellationToken)
                .ConfigureAwait(false);
        }


        private static Task<SyncCliPassResult> RunSoakPassAsync(
            SyncCliRuntime runtime,
            CancellationToken cancellationToken)
        {
            return SyncCliRuntimeFactory.RunSinglePassAsync(runtime, CreateSoakRunOptions(), cancellationToken);
        }


        private static async Task<SyncCliPassResult?> RunOptionalSoakPassAsync(
            SyncCliRuntime? runtime,
            CancellationToken cancellationToken)
        {
            return runtime is null
                ? null
                : await RunSoakPassAsync(runtime, cancellationToken).ConfigureAwait(false);
        }


        private static async Task CaptureFinalConvergenceAsync(
            SyncCliRuntime runtime,
            SyncCliRuntime? secondRuntime,
            SyncCliSoakStatistics statistics,
            CancellationToken cancellationToken)
        {
            SyncCliPassResult pass = await RunFinalConvergenceAsync(runtime, cancellationToken).ConfigureAwait(false);
            SyncCliPassResult? secondPass = secondRuntime is null
                ? null
                : await RunFinalConvergenceAsync(secondRuntime, cancellationToken).ConfigureAwait(false);
            statistics.RecordConvergence(pass, secondPass);
        }


        private static async Task<SyncCliPassResult> RunFinalConvergenceAsync(
            SyncCliRuntime runtime,
            CancellationToken cancellationToken)
        {
            SyncCliPassResult? lastPass = null;
            for (int pass = 1; pass <= MaxFinalConvergencePasses; pass++)
            {
                lastPass = await SyncCliRuntimeFactory
                    .RunSinglePassAsync(runtime, CreateSoakRunOptions(), cancellationToken)
                    .ConfigureAwait(false);
                if (IsIdle(lastPass))
                {
                    return lastPass;
                }

                if (pass >= MaxFinalConvergencePasses)
                {
                    break;
                }

                if (lastPass.Result.HasDeferredLocalPaths)
                {
                    await Task.Delay(SoakMinimumLocalUploadAge, cancellationToken).ConfigureAwait(false);
                }
            }

            return lastPass ?? throw new InvalidOperationException("Final convergence pass did not run.");
        }


        private static SyncRunOptions CreateSoakRunOptions()
        {
            return new SyncRunOptions
            {
                MinimumLocalUploadAge = SoakMinimumLocalUploadAge,
            };
        }
    }
}
