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
        private static async Task<int> RunSteadyStateRepeatAsync(WindowsVirtualFilesSmokeContext context)
        {
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            SyncPairSettings syncPair = context.SyncPair;
            int largeTreePlaceholderCount = GetLargeTreePlaceholderCount(context.StartupOptions);
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            CancellationToken cancellationToken = context.CancellationToken;

            string rootPath = syncPair.LocalRootPath;
            string largeTreePath = Path.Combine(rootPath, LargeTreeDirectoryName);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            List<RemoteFileSnapshot> remoteFiles = new(largeTreePlaceholderCount);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(largeTreePath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for steady-state repeat smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);
                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for steady-state repeat smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                List<SyncStateEntry> createdEntries = new(largeTreePlaceholderCount);
                Stopwatch createTimer = Stopwatch.StartNew();
                for (int index = 0; index < largeTreePlaceholderCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = LargeTreeDirectoryName
                        + "/file-"
                        + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                        + ".txt";
                    RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(
                        syncPair,
                        relativePath,
                        expectedContent.LongLength,
                        expectedHash);
                    ApplyLargeSmokeRemoteIdentity(request.RemoteFile, index);
                    RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(request);
                    SyncStateEntry stateEntry = CreatePlaceholderState(syncPair, request, placeholder);
                    createdEntries.Add(stateEntry);
                    remoteFiles.Add(new RemoteFileSnapshot
                    {
                        RelativePath = relativePath,
                        File = request.RemoteFile,
                    });

                    if ((index + 1) % SteadyStateProgressInterval == 0)
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

                foreach (SyncStateEntry[] batch in createdEntries.Chunk(LargeCleanupStateWriteBatchSize))
                {
                    await stateStore.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                createTimer.Stop();
                await output.WriteLineAsync(
                    FormatCheck(true, "Steady-state repeat smoke persisted placeholder baseline.")
                    + " files="
                    + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + ", elapsedMs="
                    + createTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                GuardLocalScanner scanner = new();
                LargeStateFirstRemoteCrawler crawler = new(syncPair.RemoteRootNodeId, remoteFiles);
                NoTransferRemoteFileSynchronizer noTransfers = new();
                GuardRemoteFilePlaceholderWriter placeholderWriter = new();
                SyncEngine engine = new(
                    scanner,
                    crawler,
                    noTransfers,
                    stateStore,
                    remoteFilePlaceholderWriter: placeholderWriter);
                SyncPair syncPairCore = new()
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    LocalRootPath = syncPair.LocalRootPath,
                    RemoteRootNodeId = syncPair.RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                };
                Stopwatch syncTimer = Stopwatch.StartNew();
                SyncRunResult result = await engine
                    .RunOnceAsync(syncPairCore, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                syncTimer.Stop();

                bool passed = DidSteadyStateFastPathPass(
                    result,
                    largeTreePlaceholderCount,
                    scanner,
                    crawler,
                    noTransfers,
                    placeholderWriter,
                    syncTimer.Elapsed);
                failures += await WriteOutcomeAsync(
                        output,
                        passed,
                        "Steady-state repeat pass used scoped path validation without local placeholder-tree scanning.",
                        "Steady-state repeat pass did not stay on the state-first fast path.",
                        FormatSteadyStateRepeatDetails(
                            largeTreePlaceholderCount,
                            result,
                            scanner,
                            crawler,
                            noTransfers,
                            placeholderWriter,
                            syncTimer.ElapsedMilliseconds))
                    .ConfigureAwait(false);
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

        private static bool DidSteadyStateFastPathPass(
            SyncRunResult result,
            int expectedPresenceProbeCalls,
            GuardLocalScanner scanner,
            LargeStateFirstRemoteCrawler crawler,
            NoTransferRemoteFileSynchronizer remoteFiles,
            GuardRemoteFilePlaceholderWriter placeholderWriter,
            TimeSpan elapsed)
        {
            return !result.RequiresUserAction
                && scanner.FullScanCalls == 0
                && scanner.MetadataTreeScanCalls == 0
                && scanner.PathLookupCalls == 1
                && scanner.PresenceProbeCalls == expectedPresenceProbeCalls
                && crawler.StreamingCrawlCalls == 1
                && crawler.SnapshotCrawlCalls == 0
                && remoteFiles.TransferCalls == 0
                && placeholderWriter.PlaceholderWriteCalls == 0
                && elapsed <= SteadyStateRepeatTimeout;
        }

        private static string FormatSteadyStateRepeatDetails(
            int fileCount,
            SyncRunResult result,
            GuardLocalScanner scanner,
            LargeStateFirstRemoteCrawler crawler,
            NoTransferRemoteFileSynchronizer remoteFiles,
            GuardRemoteFilePlaceholderWriter placeholderWriter,
            long elapsedMilliseconds)
        {
            return "files="
                + fileCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                + ", requiresAction="
                + result.RequiresUserAction.ToString()
                + ", syncElapsedMs="
                + elapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", streamingCrawls="
                + crawler.StreamingCrawlCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", snapshotCrawls="
                + crawler.SnapshotCrawlCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", fullLocalScans="
                + scanner.FullScanCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", metadataTreeScans="
                + scanner.MetadataTreeScanCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", pathLookups="
                + scanner.PathLookupCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", transfers="
                + remoteFiles.TransferCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", placeholderWrites="
                + placeholderWriter.PlaceholderWriteCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", presenceProbes="
                + scanner.PresenceProbeCalls.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<int> RunInitialStreamingLoggingAsync(WindowsVirtualFilesSmokeContext context)
        {
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            SyncPairSettings syncPair = context.SyncPair;
            int largeTreePlaceholderCount = GetLargeTreePlaceholderCount(context.StartupOptions);
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            CancellationToken cancellationToken = context.CancellationToken;

            string rootPath = syncPair.LocalRootPath;
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            IReadOnlyList<RemoteFileSnapshot> remoteFiles = CreateLargeTreeRemoteFiles(syncPair, largeTreePlaceholderCount);
            RemoteDirectorySnapshot largeTreeDirectory = CreateLargeTreeRemoteDirectory(syncPair);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for initial VFS logging smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);
                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for initial VFS logging smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                using DesktopTraceLoggerFactory loggerFactory = new();
                ILogger<SyncEngine> syncLogger = loggerFactory.CreateLogger<SyncEngine>();
                ILogger<DesktopCloudFilesPlaceholderWriter> placeholderLogger =
                    loggerFactory.CreateLogger<DesktopCloudFilesPlaceholderWriter>();
                DesktopCloudFilesPlaceholderWriter placeholderWriter = new(
                    cloudFilesAdapter: cloudFiles,
                    getCapabilities: static () => new SyncPairModeCapabilitySnapshot(true, "Windows Cloud Files API is available."),
                    logger: placeholderLogger);
                SyncEngine syncEngine = new(
                    new LocalFileScanner(),
                    new InitialStreamingLoggingRemoteCrawler(
                        syncPair.RemoteRootNodeId,
                        largeTreeDirectory,
                        remoteFiles),
                    new NoTransferRemoteFileSynchronizer(),
                    stateStore,
                    remoteFilePlaceholderWriter: placeholderWriter,
                    logger: syncLogger);
                SyncPair syncPairCore = new()
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    LocalRootPath = syncPair.LocalRootPath,
                    RemoteRootNodeId = syncPair.RemoteRootNodeId,
                    MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
                };
                RecordingSyncRunProgress runProgress = new();

                DesktopRuntimeHealthSnapshot beforeStreamingHealth = CreateRuntimeHealthSnapshot();
                Stopwatch syncTimer = Stopwatch.StartNew();
                SyncRunResult result = await syncEngine
                    .RunOnceAsync(
                        syncPairCore,
                        new SyncRunOptions { RunProgress = runProgress },
                        cancellationToken)
                    .ConfigureAwait(false);
                syncTimer.Stop();
                DesktopRuntimeHealthSnapshot afterStreamingHealth = CreateRuntimeHealthSnapshot();

                IReadOnlyList<SyncStateEntry> state = await stateStore
                    .LoadPairAsync(syncPair.Id.ToString("D"), cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        IsInitialStreamingBaselineValid(result, state.Count, largeTreePlaceholderCount),
                        "Initial VFS streaming run created a large placeholder baseline without per-placeholder activities.",
                        "files="
                        + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", stateRows="
                        + state.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", activities="
                        + result.TotalActivityCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", syncElapsedMs="
                        + syncTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                IReadOnlyList<SyncRunProgress> progressSamples = runProgress.Snapshot();
                IReadOnlyList<SyncRunProgress> placeholderProgress = progressSamples
                    .Where(static progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                    .ToArray();
                SyncRunProgress? finalPlaceholderProgress = placeholderProgress.Count > 0
                    ? placeholderProgress[^1]
                    : null;
                int expectedPlaceholderProgressItems = largeTreePlaceholderCount + 1;
                bool hasProgressSummary = HasExpectedInitialStreamingProgress(
                    progressSamples,
                    finalPlaceholderProgress,
                    expectedPlaceholderProgressItems);
                failures += await WriteCheckAsync(
                        output,
                        hasProgressSummary,
                        "Initial VFS streaming progress stayed on placeholder creation and completed cleanly.",
                        "samples="
                        + progressSamples.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", placeholderSamples="
                        + placeholderProgress.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                        + ", finalItems="
                        + (finalPlaceholderProgress is null
                            ? "0"
                            : finalPlaceholderProgress.FilesCompleted.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                        + "/"
                        + (finalPlaceholderProgress is null
                            ? "0"
                            : finalPlaceholderProgress.FilesTotal.GetValueOrDefault().ToString(
                                "N0",
                                System.Globalization.CultureInfo.InvariantCulture))
                        + ", completed="
                        + progressSamples.Any(static progress => progress.Stage == SyncRunProgressStage.Completed && progress.IsCompleted).ToString()
                        + ", localScanSamples="
                        + progressSamples.Count(static progress => progress.Stage == SyncRunProgressStage.ScanningLocal).ToString(
                            "N0",
                            System.Globalization.CultureInfo.InvariantCulture)
                        + ", remoteScanSamples="
                        + progressSamples.Count(static progress => progress.Stage == SyncRunProgressStage.ScanningRemote).ToString(
                            "N0",
                            System.Globalization.CultureInfo.InvariantCulture)
                        + ", activities="
                        + result.TotalActivityCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        HasValidRuntimeHealth(afterStreamingHealth),
                        "Initial VFS runtime health captured.",
                        "before="
                        + FormatRuntimeHealth(beforeStreamingHealth)
                        + ", after="
                        + FormatRuntimeHealth(afterStreamingHealth))
                    .ConfigureAwait(false);

                Trace.Flush();
                failures += await VerifyInitialStreamingLogMetricsAsync(
                        paths.LogFilePath,
                        output,
                        largeTreePlaceholderCount,
                        cancellationToken)
                    .ConfigureAwait(false);
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

        private static bool IsInitialStreamingBaselineValid(
            SyncRunResult result,
            int stateEntryCount,
            int expectedFileCount)
        {
            return !result.RequiresUserAction
                && result.TotalActivityCount == 0
                && stateEntryCount == expectedFileCount + 1;
        }

        private static bool HasExpectedInitialStreamingProgress(
            IReadOnlyList<SyncRunProgress> progressSamples,
            SyncRunProgress? finalPlaceholderProgress,
            int expectedItemCount)
        {
            return finalPlaceholderProgress is not null
                && progressSamples.Any(static progress => progress.Stage == SyncRunProgressStage.Completed && progress.IsCompleted)
                && !progressSamples.Any(static progress =>
                    progress.Stage is SyncRunProgressStage.ScanningLocal or SyncRunProgressStage.ScanningRemote)
                && finalPlaceholderProgress.FilesCompleted == expectedItemCount
                && finalPlaceholderProgress.FilesTotal == expectedItemCount;
        }

        private static bool HasValidRuntimeHealth(DesktopRuntimeHealthSnapshot runtimeHealth)
        {
            return runtimeHealth.WorkingSetBytes > 0
                && runtimeHealth.ThreadCount.GetValueOrDefault() > 0
                && runtimeHealth.HandleCount.GetValueOrDefault() > 0;
        }

        private static async Task<int> RecordSmokeFailureAsync(
            TextWriter output,
            int failures,
            Exception exception)
        {
            await output.WriteLineAsync(
                FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                .ConfigureAwait(false);
            return failures + 1;
        }

        internal static async Task<int> WriteSmokeResultAsync(
            TextWriter output,
            WindowsCloudFilesDiagnostics diagnostics,
            int failures)
        {
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

            bool passed = failures == 0;
            await output.WriteLineAsync(passed ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static async Task<int> WriteCheckAsync(
            TextWriter output,
            bool passed,
            string label,
            string details)
        {
            await output.WriteLineAsync(
                    FormatCheck(passed, label)
                    + " "
                    + CleanSingleLine(details))
                .ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static async Task<int> WriteOutcomeAsync(
            TextWriter output,
            bool passed,
            string passedLabel,
            string failedLabel,
            string details = "")
        {
            string label = passed ? passedLabel : failedLabel;
            return await WriteCheckAsync(output, passed, label, details).ConfigureAwait(false);
        }

        private static async Task<int> VerifyInitialStreamingLogMetricsAsync(
            string logFilePath,
            TextWriter output,
            int largeTreePlaceholderCount,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(logFilePath))
            {
                await output.WriteLineAsync(
                        FormatCheck(false, "Initial VFS logging smoke wrote a trace log.")
                        + " log=missing")
                    .ConfigureAwait(false);
                return 1;
            }

            string logText = await File.ReadAllTextAsync(logFilePath, cancellationToken).ConfigureAwait(false);
            string? completionLog = logText
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .LastOrDefault(static line => line.Contains(
                    "Completed initial streaming Windows virtual-files population",
                    StringComparison.Ordinal));
            string expectedFileCount = largeTreePlaceholderCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string[] requiredMetrics =
            [
                "1 directories discovered",
                "dirs/sec",
                expectedFileCount + " files discovered",
                "files/sec",
                "remote pages read=",
                "remote page latency total=",
                expectedFileCount + " placeholders created or refreshed",
                "placeholders/sec",
                "state writes " + expectedFileCount + " file rows",
                "file write batches",
                "directory rows 1",
                "state write rate=",
                "rows/sec",
                "managed heap start=",
                "peak=",
                "activities retained 0/0",
            ];
            bool hasMetrics = completionLog is not null
                && requiredMetrics.All(metric => completionLog.Contains(metric, StringComparison.Ordinal));
            await output.WriteLineAsync(
                    FormatCheck(hasMetrics, "Initial VFS trace log contains large-run metrics.")
                    + " hasCompletionLog="
                    + (completionLog is not null).ToString()
                    + ", log="
                    + logFilePath)
                .ConfigureAwait(false);
            if (hasMetrics)
            {
                await output.WriteLineAsync("Metric excerpt: " + CleanSingleLine(completionLog!)).ConfigureAwait(false);
            }

            return hasMetrics ? 0 : 1;
        }
    }
}
