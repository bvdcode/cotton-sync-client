// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync;
using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static class SyncCliSoakCommandRunner
    {
        private const int MaxFinalConvergencePasses = 6;
        private static readonly TimeSpan SoakMinimumLocalUploadAge = TimeSpan.FromSeconds(3);

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

        private static SyncCliConnectionOptions? ReadSecondClientOptions(
            IReadOnlyList<string> args,
            SyncCliConnectionOptions firstClientOptions,
            TextWriter error)
        {
            string? localRoot = SyncCliOptionsReader.ReadOption(args, "--second-local-root");
            string? syncPairId = SyncCliOptionsReader.ReadOption(args, "--second-sync-pair");
            string? databasePath = SyncCliOptionsReader.ReadOption(args, "--second-database");
            if (string.IsNullOrWhiteSpace(localRoot)
                && string.IsNullOrWhiteSpace(syncPairId)
                && string.IsNullOrWhiteSpace(databasePath))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(localRoot)
                || string.IsNullOrWhiteSpace(syncPairId)
                || string.IsNullOrWhiteSpace(databasePath))
            {
                error.WriteLine(
                    "Two-client sync-soak requires --second-local-root, --second-sync-pair, and --second-database together.");
                return null;
            }

            if (SyncCliPath.AreSameOrNested(firstClientOptions.LocalRoot, localRoot))
            {
                error.WriteLine("Two-client sync-soak local roots must be different and non-nested.");
                return null;
            }

            if (string.Equals(firstClientOptions.SyncPairId, syncPairId.Trim(), StringComparison.Ordinal))
            {
                error.WriteLine("Two-client sync-soak sync pair ids must be different.");
                return null;
            }

            if (SyncCliPath.AreSame(firstClientOptions.DatabasePath, databasePath))
            {
                error.WriteLine("Two-client sync-soak databases must be different.");
                return null;
            }

            return firstClientOptions with
            {
                LocalRoot = localRoot,
                SyncPairId = syncPairId.Trim(),
                DatabasePath = databasePath,
            };
        }

        private static bool HasSecondClientOption(IReadOnlyList<string> args)
        {
            return SyncCliOptionsReader.ReadOption(args, "--second-local-root") is not null
                || SyncCliOptionsReader.ReadOption(args, "--second-sync-pair") is not null
                || SyncCliOptionsReader.ReadOption(args, "--second-database") is not null;
        }

        private static bool ShouldRunNextSoakIteration(
            int completedIterations,
            int? maxIterations,
            DateTime? stopAtUtc)
        {
            if (maxIterations.HasValue && completedIterations >= maxIterations.Value)
            {
                return false;
            }

            return !stopAtUtc.HasValue || DateTime.UtcNow < stopAtUtc.Value || completedIterations == 0;
        }

        private static TimeSpan GetNextSoakDelay(int intervalSeconds, DateTime? stopAtUtc)
        {
            TimeSpan interval = TimeSpan.FromSeconds(intervalSeconds);
            if (!stopAtUtc.HasValue)
            {
                return interval;
            }

            TimeSpan remaining = stopAtUtc.Value - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return remaining >= interval ? interval : remaining;
        }

        private static async Task WriteIterationAsync(
            TextWriter output,
            int iteration,
            SyncCliPassResult pass,
            SyncCliPassResult? secondPass,
            SyncCliSoakStatistics statistics,
            TimeSpan iterationElapsed)
        {
            string metrics = ", workingSetBytes=" + statistics.GetWorkingSetBytes().ToStringInvariant()
                + ", managedMemoryBytes=" + statistics.GetManagedMemoryBytes().ToStringInvariant()
                + ", elapsedSeconds=" + iterationElapsed.TotalSeconds.ToStringInvariant();
            if (secondPass is null)
            {
                await output
                    .WriteLineAsync(
                        "Iteration " + iteration.ToStringInvariant()
                        + ": activities=" + GetActivityCount(pass).ToStringInvariant()
                        + ", deferredLocalPaths=" + pass.Result.DeferredLocalPaths.Count.ToStringInvariant()
                        + ", stateEntries=" + pass.StateEntries.Count.ToStringInvariant()
                        + metrics)
                    .ConfigureAwait(false);
                return;
            }

            await output
                .WriteLineAsync(
                        "Iteration " + iteration.ToStringInvariant()
                        + ": clientAActivities=" + GetActivityCount(pass).ToStringInvariant()
                        + ", clientADeferredLocalPaths=" + pass.Result.DeferredLocalPaths.Count.ToStringInvariant()
                        + ", clientBActivities=" + GetActivityCount(secondPass).ToStringInvariant()
                        + ", clientBDeferredLocalPaths=" + secondPass.Result.DeferredLocalPaths.Count.ToStringInvariant()
                        + ", clientAStateEntries=" + pass.StateEntries.Count.ToStringInvariant()
                        + ", clientBStateEntries=" + secondPass.StateEntries.Count.ToStringInvariant()
                        + metrics)
                .ConfigureAwait(false);
        }

        private static async Task WriteProbeFileAsync(
            string localRoot,
            string relativePath,
            int iteration,
            CancellationToken cancellationToken)
        {
            string fullPath = Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string content = "Cotton Sync soak probe" + Environment.NewLine
                + "Iteration: " + iteration.ToStringInvariant() + Environment.NewLine
                + "UTC: " + SyncCliFormat.FormatUtc(DateTime.UtcNow) + Environment.NewLine;
            await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteSummaryAsync(
            TextWriter output,
            SyncCliSoakStatistics statistics)
        {
            DateTime completedAtUtc = DateTime.UtcNow;
            TimeSpan elapsed = completedAtUtc - statistics.StartedAt;
            TimeSpan cpu = statistics.GetCpuTime();
            double cpuUtilizationPercent = CalculateCpuUtilizationPercent(cpu, elapsed);
            long completedWorkingSetBytes = statistics.GetWorkingSetBytes();
            long completedManagedMemoryBytes = statistics.GetManagedMemoryBytes();
            statistics.CaptureResourcePeaks();

            await output.WriteLineAsync("Completed UTC: " + SyncCliFormat.FormatUtc(completedAtUtc)).ConfigureAwait(false);
            await output.WriteLineAsync("Elapsed seconds: " + elapsed.TotalSeconds.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("CPU seconds: " + cpu.TotalSeconds.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("CPU utilization percent: " + cpuUtilizationPercent.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Start working set bytes: " + statistics.StartedWorkingSetBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("End working set bytes: " + completedWorkingSetBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Working set growth bytes: " + (completedWorkingSetBytes - statistics.StartedWorkingSetBytes).ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Peak working set bytes: " + statistics.PeakWorkingSetBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Peak working set growth bytes: " + (statistics.PeakWorkingSetBytes - statistics.StartedWorkingSetBytes).ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Start managed memory bytes: " + statistics.StartedManagedMemoryBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("End managed memory bytes: " + completedManagedMemoryBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Managed memory growth bytes: " + (completedManagedMemoryBytes - statistics.StartedManagedMemoryBytes).ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Peak managed memory bytes: " + statistics.PeakManagedMemoryBytes.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Peak managed memory growth bytes: " + (statistics.PeakManagedMemoryBytes - statistics.StartedManagedMemoryBytes).ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Iterations completed: " + statistics.CompletedIterations.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Iteration seconds total: " + statistics.TotalIterationElapsed.TotalSeconds.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Iteration seconds average: " + CalculateAverageIterationSeconds(statistics.TotalIterationElapsed, statistics.CompletedIterations).ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Iteration seconds max: " + statistics.LongestIterationElapsed.TotalSeconds.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Total activities: " + statistics.TotalActivities.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Sync errors: " + statistics.SyncErrors.ToStringInvariant()).ConfigureAwait(false);
            await output.WriteLineAsync("Final convergence activities: " + FormatOptionalInt(statistics.FinalConvergenceActivities)).ConfigureAwait(false);
            await output.WriteLineAsync("Final state entries: " + FormatOptionalInt(statistics.FinalStateEntries)).ConfigureAwait(false);
            await output.WriteLineAsync("Converged: " + (statistics.IsConverged ? "yes" : "no")).ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + statistics.FailureCount.ToStringInvariant()).ConfigureAwait(false);
        }

        private static string FormatOptionalInt(int? value)
        {
            return value.HasValue ? value.Value.ToStringInvariant() : "not run";
        }

        private static bool IsIdle(SyncCliPassResult pass)
        {
            return GetActivityCount(pass) == 0 && !pass.Result.HasDeferredLocalPaths;
        }

        private static int GetActivityCount(SyncCliPassResult pass)
        {
            return pass.Result.TotalActivityCount;
        }

        private static double CalculateAverageIterationSeconds(TimeSpan totalIterationElapsed, int completedIterations)
        {
            return completedIterations > 0
                ? totalIterationElapsed.TotalSeconds / completedIterations
                : 0;
        }

        private static double CalculateCpuUtilizationPercent(TimeSpan cpu, TimeSpan elapsed)
        {
            return elapsed.TotalSeconds > 0
                ? cpu.TotalSeconds / elapsed.TotalSeconds * 100
                : 0;
        }

        private static string FormatException(Exception exception)
        {
            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? "No details."
                : exception.Message.ReplaceLineEndings(" ");
            return exception.GetType().Name + ": " + message;
        }
    }
}
