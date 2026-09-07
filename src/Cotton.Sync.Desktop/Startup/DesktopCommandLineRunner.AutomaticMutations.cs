// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<int> RunAutomaticLiveChangesAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            string firstContent = "Automatic overwrite from client A.";
            string secondContent = "Automatic overwrite from client B with different bytes.";
            string pausedContent = "Local edit retained while client A was paused.";
            await WriteFileAsync(options.LocalRoot!, LocalUploadPath, firstContent, cancellationToken).ConfigureAwait(false);
            failures += await WaitForAutomaticContentAsync(options, session, firstContent,
                "Automatic client A overwrite reached both clients without manual sync.", output, cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(options.SecondLocalRoot!, LocalUploadPath, secondContent, cancellationToken).ConfigureAwait(false);
            failures += await WaitForAutomaticContentAsync(options, session, secondContent,
                "Automatic client B overwrite reached both clients without manual sync.", output, cancellationToken).ConfigureAwait(false);

            await session.FirstController.PauseAllAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DesktopShellSnapshot paused = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
                DesktopSyncPairSnapshot? pair = paused.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair!.Id);
                bool isPaused = pair?.Status == "Paused";
                await output.WriteLineAsync(FormatCheck(isPaused, "Client A acknowledged pause before a local edit.")).ConfigureAwait(false);
                if (!isPaused)
                {
                    return failures + 1;
                }

                await WriteFileAsync(options.LocalRoot!, LocalUploadPath, pausedContent, cancellationToken).ConfigureAwait(false);
                DateTime deadline = DateTime.UtcNow + DesktopLocalQuietWindow + DesktopLocalQuietWindow;
                bool secondUnchanged = true;
                do
                {
                    TextReadSnapshot second = await TryReadAllTextForLiveSmokeAsync(
                        FullPath(options.SecondLocalRoot!, LocalUploadPath), cancellationToken).ConfigureAwait(false);
                    secondUnchanged &= second.Exists && second.Read && second.Content == secondContent;
                    await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
                }
                while (DateTime.UtcNow < deadline);
                await output.WriteLineAsync(FormatCheck(secondUnchanged,
                    "Paused client kept its local edit from propagating to the second client.")).ConfigureAwait(false);
                if (!secondUnchanged)
                {
                    failures++;
                }
            }
            finally
            {
                await session.FirstController.ResumeAllAsync(CancellationToken.None).ConfigureAwait(false);
            }

            failures += await WaitForAutomaticContentAsync(options, session, pausedContent,
                "Resume delivered the paused edit automatically and both clients returned to idle.", output, cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> WaitForAutomaticContentAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            string content,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + PropagationTimeout;
            int stableObservations = 0;
            PresenceSnapshot presence;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                presence = await CapturePresenceAsync(options.LocalRoot!, options.SecondLocalRoot!,
                    LocalUploadPath, content, cancellationToken).ConfigureAwait(false);
                DesktopShellSnapshot first = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
                DesktopShellSnapshot second = await session.SecondController.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool idle = AreLiveSmokePairsIdle(
                    first.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair!.Id),
                    second.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair!.Id));
                stableObservations = presence.Passed && idle ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    await output.WriteLineAsync(FormatCheck(true, label)).ConfigureAwait(false);
                    return 0;
                }

                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            await output.WriteLineAsync(FormatCheck(false, label) + " " + presence.Details).ConfigureAwait(false);
            return 1;
        }
    }
}
