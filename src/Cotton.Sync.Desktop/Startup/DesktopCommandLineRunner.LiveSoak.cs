// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static readonly TimeSpan LiveSoakMutationInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan LiveSoakIdleInterval = TimeSpan.FromSeconds(30);

        private static async Task<int> RunLiveSyncSoakAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (options.LiveSyncSmokeSoakDuration == TimeSpan.Zero)
            {
                return 0;
            }

            int failures = 0;
            int mutations = 0;
            int idleChecks = 0;
            long started = Stopwatch.GetTimestamp();
            TimeSpan activeDuration = options.LiveSyncSmokeSoakDuration / 2;
            string content = string.Empty;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                mutations++;
                string root = options.LocalRoot!;
                if (mutations % 2 == 0)
                {
                    root = options.SecondLocalRoot!;
                }

                content = "Cotton live soak revision " + mutations.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WriteFileAsync(root, LocalUploadPath, content, cancellationToken).ConfigureAwait(false);
                failures += await WaitForAutomaticContentAsync(options, session, content,
                    "Live soak mutation converged automatically: " + mutations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    output, cancellationToken).ConfigureAwait(false);
                if (failures != 0)
                {
                    return failures;
                }

                await DelayWithinLiveSoakAsync(started, activeDuration, LiveSoakMutationInterval, cancellationToken).ConfigureAwait(false);
            }
            while (Stopwatch.GetElapsedTime(started) < activeDuration);

            await output.WriteLineAsync("Live soak entered idle verification.").ConfigureAwait(false);
            while (Stopwatch.GetElapsedTime(started) < options.LiveSyncSmokeSoakDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                idleChecks++;
                failures += await WaitForAutomaticContentAsync(options, session, content,
                    "Live soak idle contents and status remained stable: " + idleChecks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    output, cancellationToken).ConfigureAwait(false);
                if (failures != 0)
                {
                    return failures;
                }

                await DelayWithinLiveSoakAsync(started, options.LiveSyncSmokeSoakDuration,
                    LiveSoakIdleInterval, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync("Live soak completed: mutations=" + mutations.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", idleChecks=" + idleChecks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", elapsedSeconds=" + Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return failures;
        }

        private static Task DelayWithinLiveSoakAsync(long started, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
        {
            TimeSpan remaining = duration - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            return Task.Delay(remaining < interval ? remaining : interval, cancellationToken);
        }
    }
}
