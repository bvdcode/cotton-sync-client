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
    }
}
