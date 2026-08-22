// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync;
using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliSoakCommandRunner
    {

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
