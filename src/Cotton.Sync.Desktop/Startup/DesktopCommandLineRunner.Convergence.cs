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
        private static async Task<int> VerifyIdleAsync(
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DesktopShellSnapshot firstSnapshot = await firstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondSnapshot = await secondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncPairSnapshot? firstPair = firstSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == firstPairId);
            DesktopSyncPairSnapshot? secondPair = secondSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == secondPairId);
            bool passed = AreLiveSmokePairsIdle(firstPair, secondPair);
            await output.WriteLineAsync(
                FormatCheck(passed, label)
                + " firstStatus=" + (firstPair?.Status ?? "<missing>")
                + ", secondStatus=" + (secondPair?.Status ?? "<missing>")).ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static bool AreLiveSmokePairsIdle(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair)
        {
            return IsIdleWithoutError(firstPair) && IsIdleWithoutError(secondPair);
        }

        private static bool IsIdleWithoutError(DesktopSyncPairSnapshot? pair)
        {
            return pair is not null
                && string.Equals(pair.Status, "Idle", StringComparison.Ordinal)
                && pair.LastError is null;
        }

        private static async Task<int> WaitForLiveSmokeConvergenceAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow + InitialConvergenceTimeout;
            int attempts = 0;
            int stableObservations = 0;
            LiveSyncSmokeConvergenceSnapshot snapshot;
            await firstController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            await secondController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            do
            {
                attempts++;
                if (attempts > 1 && attempts % InitialConvergenceSyncRefreshInterval == 0)
                {
                    await firstController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                    await secondController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                }

                snapshot = await CaptureLiveSmokeConvergenceAsync(
                        startupOptions,
                        seededLocalFiles,
                        firstController,
                        secondController,
                        firstPairId,
                        secondPairId,
                        cancellationToken)
                    .ConfigureAwait(false);
                stableObservations = snapshot.Passed ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Initial desktop sync reached stable convergence.")
                        + " attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + snapshot.Details).ConfigureAwait(false);
                    return 0;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    break;
                }

                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);

            await output.WriteLineAsync(
                FormatCheck(false, "Initial desktop sync reached stable convergence.")
                + " attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", " + snapshot.Details).ConfigureAwait(false);
            return 1;
        }

        private static async Task<LiveSyncSmokeConvergenceSnapshot> CaptureLiveSmokeConvergenceAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            CancellationToken cancellationToken)
        {
            string[] localRoots = [startupOptions.LocalRoot!, startupOptions.SecondLocalRoot!];
            IReadOnlyDictionary<string, string> expectedHashes = BuildExpectedLiveSmokeHashes(
                localRoots,
                seededLocalFiles);
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> hashReads =
                await LiveSyncSmokeFileHashReader.ReadAsync(expectedHashes.Keys, cancellationToken)
                    .ConfigureAwait(false);
            (int availableFiles, int hashMismatches, int readFailures) = EvaluateLiveSmokeHashes(
                expectedHashes,
                hashReads);
            DesktopShellSnapshot firstSnapshot = await firstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondSnapshot = await secondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncPairSnapshot? firstPair = firstSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == firstPairId);
            DesktopSyncPairSnapshot? secondPair = secondSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == secondPairId);
            int expectedFiles = seededLocalFiles.Count * localRoots.Length;
            return new LiveSyncSmokeConvergenceSnapshot(
                LiveSmokeConverged(
                    firstPair,
                    secondPair,
                    availableFiles,
                    expectedFiles,
                    hashMismatches,
                    readFailures),
                FormatLiveSmokeConvergenceDetails(
                    firstPair,
                    secondPair,
                    availableFiles,
                    expectedFiles,
                    hashMismatches,
                    readFailures));
        }

        private static bool LiveSmokeConverged(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair,
            int availableFiles,
            int expectedFiles,
            int hashMismatches,
            int readFailures)
        {
            bool pairsIdle = IsSuccessfullySyncedIdlePair(firstPair)
                && IsSuccessfullySyncedIdlePair(secondPair);
            bool filesConverged = availableFiles == expectedFiles
                && hashMismatches == 0
                && readFailures == 0;
            return pairsIdle && filesConverged;
        }

        private static string FormatLiveSmokeConvergenceDetails(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair,
            int availableFiles,
            int expectedFiles,
            int hashMismatches,
            int readFailures)
        {
            return "availableSeedFiles="
                + availableFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + expectedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", hashMismatches=" + hashMismatches.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", readFailures=" + readFailures.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", firstStatus=" + (firstPair?.Status ?? "<missing>")
                + ", secondStatus=" + (secondPair?.Status ?? "<missing>");
        }

        private static IReadOnlyDictionary<string, string> BuildExpectedLiveSmokeHashes(
            IEnumerable<string> localRoots,
            IEnumerable<LiveSyncSmokeSeededLocalFile> seededLocalFiles)
        {
            Dictionary<string, string> expectedHashes = new(StringComparer.OrdinalIgnoreCase);
            foreach (LiveSyncSmokeSeededLocalFile file in seededLocalFiles)
            {
                foreach (string localRoot in localRoots)
                {
                    string fullPath = FullPath(localRoot, file.RelativePath);
                    if (File.Exists(fullPath))
                    {
                        expectedHashes[fullPath] = file.Sha256;
                    }
                }
            }

            return expectedHashes;
        }

        private static (int AvailableFiles, int HashMismatches, int ReadFailures) EvaluateLiveSmokeHashes(
            IReadOnlyDictionary<string, string> expectedHashes,
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> hashReads)
        {
            int availableFiles = 0;
            int hashMismatches = 0;
            int readFailures = 0;
            foreach ((string fullPath, string expectedHash) in expectedHashes)
            {
                if (!hashReads.TryGetValue(fullPath, out LiveSyncSmokeFileHashReadResult? read)
                    || read.Sha256 is null)
                {
                    readFailures++;
                    continue;
                }

                if (string.Equals(read.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    availableFiles++;
                }
                else
                {
                    hashMismatches++;
                }
            }

            return (availableFiles, hashMismatches, readFailures);
        }

        private static bool IsSuccessfullySyncedIdlePair(DesktopSyncPairSnapshot? pair)
        {
            return pair is not null
                && string.Equals(pair.Status, "Idle", StringComparison.Ordinal)
                && pair.LastSyncedAtUtc.HasValue
                && pair.LastError is null;
        }

        private static async Task<int> VerifySeededLocalFilesAsync(
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> files,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (files.Count == 0)
            {
                return 0;
            }

            List<string> failures = [];
            foreach (LiveSyncSmokeSeededLocalFile file in files)
            {
                if (!File.Exists(file.FullPath))
                {
                    failures.Add(file.RelativePath + "=missing");
                    continue;
                }

                string actualHash = await ComputeFileSha256Async(file.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(file.RelativePath + "=sha256-mismatch:" + actualHash);
                }
            }

            bool passed = failures.Count == 0;
            await output.WriteLineAsync(
                FormatCheck(passed, label)
                + " files=" + files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (passed ? string.Empty : ", " + string.Join(", ", failures))).ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static async Task<int> CountStateEntriesAsync(
            DesktopAppPaths paths,
            Guid syncPairId,
            CancellationToken cancellationToken)
        {
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncStateEntry> entries = await stateStore
                .LoadPairAsync(syncPairId.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            return entries.Count;
        }
    }
}
