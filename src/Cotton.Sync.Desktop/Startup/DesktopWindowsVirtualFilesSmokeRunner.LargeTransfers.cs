// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static async Task<int> RunLargeHydrationAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesNativeApi? nativeApi,
            SyncPairSettings syncPair,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (nativeApi is null)
            {
                await output.WriteLineAsync(FormatCheck(false, "Large hydration smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string placeholderPath = Path.Combine(rootPath, LargeHydrationRelativePath);
            byte[] content = CreateLargeHydrationContent();
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            var contentProvider = new ChunkedSmokeContentProvider(
                content,
                LargeHydrationChunkBytes,
                TimeSpan.FromMilliseconds(8));
            var progress = new RecordingTransferProgress();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                diagnostics,
                _ => progress);
            var callbackHandler = new RecordingCallbackHandler(coordinator);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for large-file hydration smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                cloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                    syncPair,
                    LargeHydrationRelativePath,
                    content.LongLength,
                    contentHash));
                await output.WriteLineAsync(
                    FormatCheck(true, "Large remote-only placeholder exists before hydration.")
                    + " path="
                    + placeholderPath
                    + ", sizeBytes="
                    + content.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", attributes="
                    + FormatAttributes(File.GetAttributes(placeholderPath)))
                    .ConfigureAwait(false);

                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for large-file hydration smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                var hydrateTimer = Stopwatch.StartNew();
                FileContentHash hydrated = await ReadFileHashThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                hydrateTimer.Stop();
                IReadOnlyList<SyncTransferProgress> hydrationProgress = progress.Snapshot();
                if (string.Equals(hydrated.Sha256, contentHash, StringComparison.OrdinalIgnoreCase)
                    && hydrationProgress.Count >= 4
                    && HasIntermediateProgress(hydrationProgress)
                    && IsMonotonicProgress(hydrationProgress)
                    && contentProvider.DownloadCount == 1)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Large placeholder hydration reported useful progress and hydrated exact content.")
                        + " sizeBytes="
                        + hydrated.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", sha256="
                        + hydrated.Sha256
                        + ", progressSamples="
                        + hydrationProgress.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", elapsedMs="
                        + hydrateTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Large placeholder hydration progress or content verification failed.")
                        + " expectedSha256="
                        + contentHash
                        + ", actualSha256="
                        + hydrated.Sha256
                        + ", progressSamples="
                        + hydrationProgress.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }

                bool cancellationProbePassed = await RunLargeHydrationCancellationProbeAsync(
                    paths,
                    output,
                    syncPair,
                    content,
                    contentHash,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationProbePassed)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Large placeholder hydration remained cancellable through the Cloud Files callback dispatcher."))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Large placeholder hydration cancellation probe failed."))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task<bool> RunLargeHydrationCancellationProbeAsync(
            DesktopAppPaths paths,
            TextWriter output,
            SyncPairSettings syncPair,
            byte[] content,
            string contentHash,
            CancellationToken cancellationToken)
        {
            var nativeApi = new RecordingCloudFilesNativeApi();
            var provider = new ChunkedSmokeContentProvider(
                content,
                LargeHydrationChunkBytes,
                TimeSpan.FromMilliseconds(50));
            var progress = new RecordingTransferProgress();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(
                provider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-cancel-temp"),
                diagnostics,
                _ => progress);
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                coordinator,
                nativeApi.TransferData,
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            RemoteFilePlaceholderRequest placeholderRequest = CreatePlaceholderRequest(
                syncPair,
                LargeHydrationRelativePath,
                content.LongLength,
                contentHash);
            byte[] identity = WindowsCloudFilesPlaceholderIdentity
                .Create(placeholderRequest, Cotton.Sync.State.SyncPath.Normalize(LargeHydrationRelativePath))
                .ToBytes();
            var request = new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(9001),
                new WindowsCloudFilesTransferKey(9002),
                new WindowsCloudFilesRequestKey(9003),
                identity,
                content.LongLength,
                0,
                content.LongLength,
                0,
                content.LongLength,
                LargeHydrationRelativePath,
                0);

            if (!dispatcher.QueueFetchData(request))
            {
                await output.WriteLineAsync(FormatCheck(false, "Large hydration cancellation probe could not queue fetch data."))
                    .ConfigureAwait(false);
                return false;
            }

            bool progressStarted = await progress.WaitForSampleCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            dispatcher.CancelFetchData(new WindowsCloudFilesCancelFetchDataRequest(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                0,
                content.LongLength));

            var drainTimer = Stopwatch.StartNew();
            while (dispatcher.PendingFetchCount > 0 && drainTimer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<SyncTransferProgress> samples = progress.Snapshot();
            bool successTransfers = nativeApi.Transfers.Any(static transfer =>
                transfer.CompletionStatus == WindowsCloudFilesTransferData.StatusSuccess);
            bool passed = progressStarted
                && dispatcher.PendingFetchCount == 0
                && provider.CancellationCount > 0
                && !successTransfers;
            await output.WriteLineAsync(
                FormatCheck(passed, "Large hydration cancellation probe drained pending fetch without success transfer.")
                + " progressStarted="
                + progressStarted.ToString()
                + ", progressSamples="
                + samples.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", providerCancellations="
                + provider.CancellationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", pendingFetches="
                + dispatcher.PendingFetchCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", transfers="
                + nativeApi.Transfers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return passed;
        }

        private static async Task<int> RunLargeTreeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            int largeTreePlaceholderCount,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string largeTreePath = Path.Combine(rootPath, LargeTreeDirectoryName);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for large-tree Explorer smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);
                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for large-tree Explorer smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);
                cloudFiles.CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, LargeTreeDirectoryName));
                await output.WriteLineAsync(
                    FormatCheck(true, "Large-tree top-level directory placeholder was initialized."))
                    .ConfigureAwait(false);

                var createTimer = Stopwatch.StartNew();
                for (int index = 0; index < largeTreePlaceholderCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = LargeTreeDirectoryName
                        + "/file-"
                        + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                        + ".txt";
                    cloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                        syncPair,
                        relativePath,
                        expectedContent.LongLength,
                        expectedHash));

                    if ((index + 1) % 1_000 == 0)
                    {
                        await output.WriteLineAsync(
                            "Progress: created "
                            + (index + 1).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                            + " / "
                            + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                            + " placeholders.")
                            .ConfigureAwait(false);
                    }
                }

                createTimer.Stop();
                await output.WriteLineAsync(
                    FormatCheck(true, "Large remote-only placeholder tree was created.")
                    + " files="
                    + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + ", elapsedMs="
                    + createTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                cloudFiles.CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, LargeTreeDirectoryName));
                cloudFiles.SetSyncRootInSyncState(syncPair);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        relativePath: null,
                        "Large-tree Cloud Files sync root status was marked in sync.",
                        allowPartialDirectory: true)
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        LargeTreeDirectoryName,
                        "Large-tree Cloud Files directory status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        largeTreePath,
                        "large-tree directory",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        Path.Combine(largeTreePath, "file-00000.txt"),
                        "large-tree first placeholder",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        Path.Combine(largeTreePath, CreateLargeTreeFileName(largeTreePlaceholderCount - 1)),
                        "large-tree last placeholder",
                        cancellationToken)
                    .ConfigureAwait(false);

                var enumerationTimer = Stopwatch.StartNew();
                int enumeratedFiles = Directory.EnumerateFiles(largeTreePath, "*.txt", SearchOption.TopDirectoryOnly).Count();
                enumerationTimer.Stop();
                if (enumeratedFiles == largeTreePlaceholderCount)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Large placeholder directory enumeration completed.")
                        + " files="
                        + enumeratedFiles.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", elapsedMs="
                        + enumerationTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Large placeholder directory enumeration returned an unexpected count.")
                        + " expected="
                        + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", actual="
                        + enumeratedFiles.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }

                if (startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder > TimeSpan.Zero)
                {
                    await output.WriteLineAsync(
                        "Holding large-tree root for "
                        + startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder.TotalSeconds.ToString(
                            "0.###",
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds; inspect "
                        + largeTreePath
                        + " in Explorer before cleanup starts.")
                        .ConfigureAwait(false);
                    await Task
                        .Delay(startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            foreach (WindowsCloudFilesDiagnosticEvent item in diagnostics.Snapshot())
            {
                await output.WriteLineAsync(
                    "Diagnostic: "
                    + item.Operation
                    + " "
                    + item.Status
                    + " "
                    + CleanSingleLine(item.Details))
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }
}
}
