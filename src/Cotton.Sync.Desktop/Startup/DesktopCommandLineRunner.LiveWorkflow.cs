// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<int> VerifyInitialLiveSyncStateAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Guid firstPairId = session.FirstPair!.Id;
            Guid secondPairId = session.SecondPair!.Id;
            int failures = await WaitForLiveSmokeConvergenceAsync(
                startupOptions,
                seededLocalFiles,
                session.FirstController,
                session.SecondController,
                firstPairId,
                secondPairId,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifyIdleAsync(
                session.FirstController,
                session.SecondController,
                firstPairId,
                secondPairId,
                "Initial desktop sync reached idle/up-to-date.",
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifySeededLocalFilesAsync(
                seededLocalFiles,
                "Pre-existing local files survived sync pair creation.",
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifyLiveSyncDiagnosticsAsync(
                session.FirstController,
                firstPairId,
                output,
                cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> VerifyLiveSyncDiagnosticsAsync(
            DesktopShellController controller,
            Guid syncPairId,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string diagnosticsBundlePath = await controller
                .ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
            LiveSyncSmokeDiagnosticsVerification verification =
                LiveSyncSmokeDiagnosticsVerifier.Verify(diagnosticsBundlePath, syncPairId);
            await output.WriteLineAsync(FormatCheck(
                verification.Passed,
                "Connected public diagnostics bundle is complete and sanitized. " + verification.Details))
                .ConfigureAwait(false);
            return verification.Passed ? 0 : 1;
        }

        private static async Task<int> RunLiveSyncMutationSequenceAsync(
            DesktopStartupOptions startupOptions,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            failures += await RunClientACreateAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBCreateAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientARenameAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBRenameAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientADeleteAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBDeleteAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> VerifyFinalLiveSyncStateAsync(
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await RunFinalConvergenceAsync(
                session.FirstController,
                session.SecondController,
                cancellationToken).ConfigureAwait(false);
            int failures = await VerifySeededLocalFilesAsync(
                seededLocalFiles,
                "Pre-existing local files survived final convergence.",
                output,
                cancellationToken).ConfigureAwait(false);
            int finalStateEntries = await CountStateEntriesAsync(
                    session.FirstPaths,
                    session.FirstPair!.Id,
                    cancellationToken)
                .ConfigureAwait(false)
                + await CountStateEntriesAsync(
                        session.SecondPaths,
                        session.SecondPair!.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            IReadOnlyList<string> expectedStatePaths = LiveSyncSmokeStateExpectation.BuildRelativePaths(
                seededLocalFiles.Select(static file => file.RelativePath));
            int expectedFinalStateEntries = expectedStatePaths.Count * 2;
            await output.WriteLineAsync("Final state entries: " + finalStateEntries.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                "Expected final state entries: "
                + expectedFinalStateEntries.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return finalStateEntries == expectedFinalStateEntries ? failures : failures + 1;
        }

        private static async Task CleanupLiveSyncSmokeAsync(
            DesktopLiveSyncSmokeSession session,
            TextWriter output)
        {
            if (session.FirstPair is not null)
            {
                await TryRemoveLiveSmokeSyncPairAsync(
                    session.FirstController,
                    session.FirstPair,
                    output,
                    "first").ConfigureAwait(false);
            }

            if (session.SecondPair is not null)
            {
                await TryRemoveLiveSmokeSyncPairAsync(
                    session.SecondController,
                    session.SecondPair,
                    output,
                    "second").ConfigureAwait(false);
            }

            if (session.FirstSignedIn)
            {
                await TrySignOutAsync(session.FirstController, output, "first").ConfigureAwait(false);
            }

            if (session.SecondSignedIn)
            {
                await TrySignOutAsync(session.SecondController, output, "second").ConfigureAwait(false);
            }
        }
    }
}
