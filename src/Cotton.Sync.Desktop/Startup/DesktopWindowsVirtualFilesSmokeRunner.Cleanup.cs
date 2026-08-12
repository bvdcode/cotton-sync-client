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
        private static async Task<int> RunTrayQuitDisconnectAsync(
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
                await output.WriteLineAsync(FormatCheck(false, "Tray quit disconnect smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedText = Encoding.UTF8.GetString(expectedContent);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            var contentProvider = new StaticSmokeContentProvider(expectedContent);
            var callbackHandler = new WindowsCloudFilesHydrationCoordinator(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                diagnostics);
            var syncPairs = new SingleSyncPairSettingsStore(syncPair);
            var connectionCoordinator = new WindowsCloudFilesSyncRootConnectionCoordinator(
                syncPairs,
                cloudFiles,
                callbackHandler);
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for tray quit disconnect smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                cloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                    syncPair,
                    RelativePlaceholderPath,
                    expectedContent.LongLength,
                    expectedHash));
                await output.WriteLineAsync(
                    FormatCheck(true, "Remote-only placeholder exists before tray quit simulation.")
                    + " path="
                    + placeholderPath
                    + ", attributes="
                    + FormatAttributes(File.GetAttributes(placeholderPath)))
                    .ConfigureAwait(false);

                await connectionCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files callbacks connected through the sync-core lifecycle component."))
                    .ConfigureAwait(false);

                await connectionCoordinator.StopAsync(cancellationToken).ConfigureAwait(false);
                FileAttributes stoppedAttributes = File.GetAttributes(placeholderPath);
                failures += await VerifyTrayQuitStoppedPlaceholderAsync(
                    output,
                    placeholderPath,
                    stoppedAttributes,
                    contentProvider.DownloadCount).ConfigureAwait(false);

                int downloadsBeforeReconnect = contentProvider.DownloadCount;
                await connectionCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files callbacks reconnected after tray quit simulation."))
                    .ConfigureAwait(false);

                string hydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
                failures += await VerifyTrayQuitReconnectedHydrationAsync(
                    output,
                    hydratedText,
                    hydratedHash,
                    expectedText,
                    expectedHash,
                    downloadsBeforeReconnect,
                    contentProvider.DownloadCount).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                failures += await StopTrayQuitConnectionAsync(connectionCoordinator, output).ConfigureAwait(false);
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task<int> VerifyTrayQuitStoppedPlaceholderAsync(
            TextWriter output,
            string placeholderPath,
            FileAttributes attributes,
            int downloadCount)
        {
            bool exists = File.Exists(placeholderPath);
            bool passed = exists && HasRecallOnDataAccess(attributes);
            string details = passed
                ? "attributes=" + FormatAttributes(attributes)
                    + ", downloads="
                    + downloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "exists=" + exists.ToString()
                    + ", attributes="
                    + (exists ? FormatAttributes(attributes) : "missing");
            return await WriteOutcomeAsync(
                output,
                passed,
                "Tray quit lifecycle stop disconnected callbacks without corrupting the placeholder.",
                "Placeholder was missing or lost online-only state after tray quit lifecycle stop.",
                details).ConfigureAwait(false);
        }

        private static async Task<int> VerifyTrayQuitReconnectedHydrationAsync(
            TextWriter output,
            string hydratedText,
            string hydratedHash,
            string expectedText,
            string expectedHash,
            int downloadsBeforeReconnect,
            int downloadsAfterReconnect)
        {
            bool passed = string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                && downloadsAfterReconnect == downloadsBeforeReconnect + 1;
            string details = passed
                ? "sha256=" + hydratedHash
                    + ", downloads="
                    + downloadsAfterReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "expectedSha256=" + expectedHash
                    + ", actualSha256=" + hydratedHash
                    + ", downloadsBeforeReconnect="
                    + downloadsBeforeReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterReconnect="
                    + downloadsAfterReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return await WriteOutcomeAsync(
                output,
                passed,
                "Reconnected callbacks hydrated exact remote content after tray quit simulation.",
                "Reconnected callbacks did not hydrate exact content after tray quit simulation.",
                details).ConfigureAwait(false);
        }

        private static async Task<int> StopTrayQuitConnectionAsync(
            WindowsCloudFilesSyncRootConnectionCoordinator connectionCoordinator,
            TextWriter output)
        {
            try
            {
                await connectionCoordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync(
                    FormatCheck(false, "Final lifecycle disconnect failed: " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<int> RunRemovePairCleanupAsync(
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
                await output.WriteLineAsync(FormatCheck(false, "Remove-pair cleanup smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            var syncPairs = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await syncPairs.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for remove-pair cleanup smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                RemoteFilePlaceholderRequest placeholderRequest = CreatePlaceholderRequest(
                    syncPair,
                    RelativePlaceholderPath,
                    expectedContent.LongLength,
                    expectedHash);
                RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(placeholderRequest);
                await stateStore
                    .UpsertAsync(CreatePlaceholderState(syncPair, placeholderRequest, placeholder), cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Registered Cloud Files root and placeholder before pair removal.")
                    + " path="
                    + placeholderPath
                    + ", attributes="
                    + FormatAttributes(File.GetAttributes(placeholderPath)))
                    .ConfigureAwait(false);

                SyncApplicationService app = CreateDeletionSmokeApplication(syncPairs, stateStore, cloudFiles);
                await app.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                await app.DeleteSyncPairAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Removing the virtual-files sync pair unregistered the Cloud Files root.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                failures += await VerifyPairDeletedAsync(syncPairs, stateStore, syncPair, output, cancellationToken)
                    .ConfigureAwait(false);

                if (!Directory.Exists(rootPath))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Removing the virtual-files sync pair removed the local placeholder root.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Removing the virtual-files sync pair left the local placeholder root behind.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task<int> RunLargeRemovePairCleanupAsync(
            DesktopAppPaths paths,
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesNativeApi? nativeApi,
            SyncPairSettings syncPair,
            int largeTreePlaceholderCount,
            WindowsCloudFilesDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (nativeApi is null)
            {
                await output.WriteLineAsync(FormatCheck(false, "Large remove-pair cleanup smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string largeTreePath = Path.Combine(rootPath, LargeTreeDirectoryName);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            var syncPairs = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(largeTreePath);
                await syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await syncPairs.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for large remove-pair cleanup smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                var createdEntries = new List<SyncStateEntry>(largeTreePlaceholderCount);
                var createTimer = Stopwatch.StartNew();
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
                    RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(request);
                    SyncStateEntry stateEntry = CreatePlaceholderState(syncPair, request, placeholder);
                    stateEntry.PlaceholderIdentity = CreateLargeSmokePlaceholderIdentity(index);
                    createdEntries.Add(stateEntry);

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

                foreach (SyncStateEntry[] batch in createdEntries.Chunk(LargeCleanupStateWriteBatchSize))
                {
                    await stateStore.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                await stateStore.SaveChangeCursorAsync(
                    new SyncChangeCursor
                    {
                        SyncPairId = syncPair.Id.ToString("D"),
                        LastCursor = largeTreePlaceholderCount,
                        UpdatedAtUtc = DateTime.UtcNow,
                    },
                    cancellationToken)
                    .ConfigureAwait(false);
                createTimer.Stop();

                SyncStateStoreDiagnostics beforeDiagnostics =
                    await stateStore.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                DesktopRuntimeHealthSnapshot beforeDeleteRuntimeHealth = CreateRuntimeHealthSnapshot();
                await output.WriteLineAsync(
                    FormatCheck(true, "Large virtual-files pair persisted placeholders before deletion.")
                    + " files="
                    + largeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + ", elapsedMs="
                    + createTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", stateEntries="
                    + beforeDiagnostics.SyncEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", cursors="
                    + beforeDiagnostics.SyncChangeCursorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", dbBytes="
                    + beforeDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                SyncApplicationService app = CreateDeletionSmokeApplication(syncPairs, stateStore, cloudFiles);
                await app.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                await app.DeleteSyncPairAsync(syncPair.Id, cancellationToken).ConfigureAwait(false);
                await app.StopSyncAsync(cancellationToken).ConfigureAwait(false);
                DesktopRuntimeHealthSnapshot afterDeleteRuntimeHealth = CreateRuntimeHealthSnapshot();
                ForceFullCollection();
                DesktopRuntimeHealthSnapshot afterGcRuntimeHealth = CreateRuntimeHealthSnapshot();

                SyncStateStoreDiagnostics afterDiagnostics =
                    await stateStore.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Large virtual-files pair deletion completed through the app lifecycle.")
                    + " stateEntries="
                    + afterDiagnostics.SyncEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", cursors="
                    + afterDiagnostics.SyncChangeCursorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", dbBytes="
                    + afterDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", freelistBytes="
                    + afterDiagnostics.FreelistBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                await output.WriteLineAsync(
                    FormatCheck(true, "Large virtual-files pair cleanup runtime health captured.")
                    + " beforeDelete="
                    + FormatRuntimeHealth(beforeDeleteRuntimeHealth)
                    + ", afterDelete="
                    + FormatRuntimeHealth(afterDeleteRuntimeHealth)
                    + ", afterGc="
                    + FormatRuntimeHealth(afterGcRuntimeHealth))
                    .ConfigureAwait(false);

                failures += await VerifyPairDeletedAsync(syncPairs, stateStore, syncPair, output, cancellationToken)
                    .ConfigureAwait(false);

                if (afterDiagnostics.FileSizeBytes < beforeDiagnostics.FileSizeBytes / 2
                    && afterDiagnostics.FreelistBytes < 1024 * 1024)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Deleting the large virtual-files pair compacted the sync-state database.")
                        + " beforeBytes="
                        + beforeDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", afterBytes="
                        + afterDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", freelistBytes="
                        + afterDiagnostics.FreelistBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Deleting the large virtual-files pair left too much sync-state storage behind.")
                        + " beforeBytes="
                        + beforeDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", afterBytes="
                        + afterDiagnostics.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", freelistBytes="
                        + afterDiagnostics.FreelistBytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }

                if (!Directory.Exists(rootPath))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Deleting the large virtual-files pair removed the local placeholder root.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Deleting the large virtual-files pair left the local placeholder root behind.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }
    }
}
