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
        private static async Task<int> RunExplorerAlwaysKeepAsync(
            WindowsVirtualFilesSmokeContext context)
        {
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            IWindowsCloudFilesNativeApi? nativeApi = context.NativeApi;
            SyncPairSettings syncPair = context.SyncPair;
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            CancellationToken cancellationToken = context.CancellationToken;
            bool restoreMissingPlaceholder =
                context.Phase == WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder;
            if (nativeApi is null)
            {
                await output.WriteLineAsync(FormatCheck(false, "Explorer Always keep smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
            (string availabilityTargetPath, string availabilityRelativePath) =
                ResolveAlwaysKeepAvailabilityTarget(rootPath, placeholderPath, restoreMissingPlaceholder);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedText = Encoding.UTF8.GetString(expectedContent);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            StaticSmokeContentProvider contentProvider = new(expectedContent);
            WindowsCloudFilesHydrationCoordinator callbackHandler = new(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                diagnostics);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for Explorer Always keep smoke.")
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
                    .UpsertAsync(
                        CreatePlaceholderState(syncPair, placeholderRequest, placeholder),
                        cancellationToken)
                    .ConfigureAwait(false);
                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files callbacks connected for Explorer Always keep smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                FileAttributes remoteOnlyAttributes = File.GetAttributes(placeholderPath);
                bool startsOnlineOnly = IsRemoteOnlyWithoutDownload(remoteOnlyAttributes, contentProvider);
                failures += await WriteCheckAsync(
                        output,
                        startsOnlineOnly,
                        "Remote-only placeholder exists before invoking Explorer Always keep.",
                        "attributes=" + FormatAttributes(remoteOnlyAttributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                failures += await PrepareMissingAlwaysKeepPlaceholderAsync(
                        output,
                        placeholderPath,
                        restoreMissingPlaceholder)
                    .ConfigureAwait(false);

                WindowsShellVerbInvocationResult verbResult = await InvokeExplorerAlwaysKeepAsync(
                        availabilityTargetPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        verbResult.Invoked,
                        "Explorer shell exposed and invoked Always keep on this device.",
                        "verb=" + (verbResult.InvokedVerbName ?? "missing")
                        + ", availableVerbs=" + string.Join("|", verbResult.AvailableVerbNames))
                    .ConfigureAwait(false);

                bool pinned = await WaitForAttributesAsync(
                    availabilityTargetPath,
                    HasPinned,
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        pinned,
                        "Cloud Files pinned state was applied for Always keep processing.",
                        "attributes=" + FormatAttributes(File.GetAttributes(availabilityTargetPath)))
                    .ConfigureAwait(false);

                WindowsVirtualFilesDehydrationPairWork hydrationWork = new(
                    new FailOnInnerSyncPairWork("Explorer Always keep smoke must not run inner sync for availability-only changes."),
                    stateStore,
                    cloudFiles,
                    new LocalFileScanner(),
                    diagnostics);
                await hydrationWork
                    .RunOnceAsync(
                        syncPair,
                        SyncRunRequest.ForLocalChangedPaths([availabilityRelativePath]),
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Production app Always keep handler processed the Cloud Files pin-state change.")
                    + " path="
                    + availabilityRelativePath)
                    .ConfigureAwait(false);

                bool becameHydratedPinned = await WaitForAttributesAsync(
                    placeholderPath,
                    IsHydratedPinnedPlaceholder,
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                    .ConfigureAwait(false);
                FileAttributes hydratedAttributes = File.GetAttributes(placeholderPath);
                failures += await WriteOutcomeAsync(
                        output,
                        becameHydratedPinned && contentProvider.DownloadCount == 1,
                        "Explorer Always keep hydrated the placeholder and kept it pinned.",
                        "Explorer Always keep did not settle to a pinned hydrated placeholder.",
                        "attributes=" + FormatAttributes(hydratedAttributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                int downloadsBeforeRead = contentProvider.DownloadCount;
                string hydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
                failures += await WriteOutcomeAsync(
                        output,
                        IsExpectedHydration(
                            hydratedText,
                            hydratedHash,
                            expectedText,
                            expectedHash,
                            contentProvider,
                            downloadsBeforeRead),
                        "Reading the Always-keep file used local hydrated content.",
                        "Reading the Always-keep file did not use the expected local content.",
                        "expectedSha256=" + expectedHash
                        + ", actualSha256=" + hydratedHash
                        + ", downloadsBeforeRead="
                        + downloadsBeforeRead.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterRead="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                SyncStateEntry? state = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), RelativePlaceholderPath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        IsExpectedHydratedState(state, expectedHash, expectedContent.LongLength),
                        "Always-keep hydration updated sync-state as hydrated.",
                        "state=" + FormatStateSummary(state))
                    .ConfigureAwait(false);

                failures += await VerifyMissingAlwaysKeepPlaceholderRepairAsync(
                        output,
                        diagnostics,
                        restoreMissingPlaceholder)
                    .ConfigureAwait(false);

                int downloadsBeforeRepeat = contentProvider.DownloadCount;
                await hydrationWork
                    .RunOnceAsync(
                        syncPair,
                        SyncRunRequest.ForLocalChangedPaths([availabilityRelativePath]),
                        cancellationToken)
                    .ConfigureAwait(false);
                FileAttributes repeatAttributes = File.GetAttributes(placeholderPath);
                SyncStateEntry? repeatedState = await stateStore
                    .GetAsync(syncPair.Id.ToString("D"), RelativePlaceholderPath, cancellationToken)
                    .ConfigureAwait(false);
                bool repeatIdempotent = IsAlwaysKeepRepeatIdempotent(
                    repeatAttributes,
                    contentProvider.DownloadCount,
                    downloadsBeforeRepeat,
                    repeatedState,
                    expectedHash,
                    expectedContent.LongLength);
                failures += await WriteCheckAsync(
                        output,
                        repeatIdempotent,
                        "Repeating Explorer Always keep on this device was idempotent.",
                        "attributes=" + FormatAttributes(repeatAttributes)
                        + ", downloadsBeforeRepeat="
                        + downloadsBeforeRepeat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterRepeat="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", state=" + FormatStateSummary(repeatedState))
                    .ConfigureAwait(false);

                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        RelativePlaceholderPath,
                        "Always-keep placeholder Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        placeholderPath,
                        "always-keep placeholder",
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

        private static (string TargetPath, string RelativePath) ResolveAlwaysKeepAvailabilityTarget(
            string rootPath,
            string placeholderPath,
            bool restoreMissingPlaceholder)
        {
            if (restoreMissingPlaceholder)
            {
                return (rootPath, ".");
            }

            return (placeholderPath, RelativePlaceholderPath);
        }

        private static bool IsRemoteOnlyWithoutDownload(
            FileAttributes attributes,
            StaticSmokeContentProvider contentProvider)
        {
            return HasRecallOnDataAccess(attributes) && contentProvider.DownloadCount == 0;
        }

        private static bool IsHydratedPinnedPlaceholder(FileAttributes attributes)
        {
            return HasPinned(attributes)
                && !HasRecallOnDataAccess(attributes)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static async Task<int> PrepareMissingAlwaysKeepPlaceholderAsync(
            TextWriter output,
            string placeholderPath,
            bool restoreMissingPlaceholder)
        {
            if (!restoreMissingPlaceholder)
            {
                return 0;
            }

            File.Delete(placeholderPath);
            return await WriteCheckAsync(
                    output,
                    !File.Exists(placeholderPath),
                    "Tracked placeholder was removed before Explorer Always keep recovery.",
                    "path=" + placeholderPath)
                .ConfigureAwait(false);
        }

        private static async Task<int> VerifyMissingAlwaysKeepPlaceholderRepairAsync(
            TextWriter output,
            WindowsCloudFilesDiagnostics diagnostics,
            bool restoreMissingPlaceholder)
        {
            if (!restoreMissingPlaceholder)
            {
                return 0;
            }

            bool repairRecorded = diagnostics.Snapshot().Any(static item =>
                item.Operation == "manual-always-keep-placeholder-repair"
                && item.Status == "completed");
            return await WriteCheckAsync(
                    output,
                    repairRecorded,
                    "Always keep restored the missing tracked placeholder before hydration.",
                    string.Empty)
                .ConfigureAwait(false);
        }

        private static bool IsExpectedHydratedState(
            SyncStateEntry? state,
            string expectedHash,
            long expectedSize)
        {
            return state is
            {
                PlaceholderHydrationState: SyncPlaceholderHydrationState.Hydrated,
                LocalContentHash: not null,
                LocalSizeBytes: not null,
            }
                && string.Equals(state.LocalContentHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                && state.LocalSizeBytes == expectedSize;
        }

        private static bool IsAlwaysKeepRepeatIdempotent(
            FileAttributes attributes,
            int downloadCount,
            int downloadsBeforeRepeat,
            SyncStateEntry? state,
            string expectedHash,
            long expectedSize)
        {
            return HasPinned(attributes)
                && !HasRecallOnDataAccess(attributes)
                && (attributes & FileAttributes.Offline) == 0
                && downloadCount == downloadsBeforeRepeat
                && IsExpectedHydratedState(state, expectedHash, expectedSize);
        }
    }
}
