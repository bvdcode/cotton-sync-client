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
        private static async Task<int> RunExplorerFreeUpSpaceAsync(
            WindowsVirtualFilesSmokeContext context)
        {
            DesktopStartupOptions startupOptions = context.StartupOptions;
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            IWindowsCloudFilesNativeApi? nativeApi = context.NativeApi;
            SyncPairSettings syncPair = context.SyncPair;
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            CancellationToken cancellationToken = context.CancellationToken;
            if (nativeApi is null)
            {
                await output.WriteLineAsync(FormatCheck(false, "Explorer Free up space smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            bool interactiveFolderSmoke = startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder > TimeSpan.Zero;
            string relativeFolderPath = "folder-free-up-space";
            string relativePlaceholderPath = interactiveFolderSmoke
                ? relativeFolderPath + "/" + RelativePlaceholderPath
                : RelativePlaceholderPath;
            string rootPath = syncPair.LocalRootPath;
            string folderPath = Path.Combine(rootPath, relativeFolderPath);
            string placeholderPath = ToFullPath(rootPath, relativePlaceholderPath);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedText = Encoding.UTF8.GetString(expectedContent);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            var contentProvider = new StaticSmokeContentProvider(expectedContent);
            var callbackHandler = new WindowsCloudFilesHydrationCoordinator(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                diagnostics);
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for Explorer Free up space smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                if (interactiveFolderSmoke)
                {
                    connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                    cloudFiles.CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, relativeFolderPath));
                }

                RemoteFilePlaceholderRequest placeholderRequest = CreatePlaceholderRequest(
                    syncPair,
                    relativePlaceholderPath,
                    expectedContent.LongLength,
                    expectedHash);
                RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(placeholderRequest);
                await stateStore
                    .UpsertAsync(
                        CreatePlaceholderState(syncPair, placeholderRequest, placeholder),
                        cancellationToken)
                    .ConfigureAwait(false);
                connection ??= cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files callbacks connected for Explorer Free up space smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                string hydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
                failures += await WriteOutcomeAsync(
                        output,
                        IsExpectedHydration(hydratedText, hydratedHash, expectedText, expectedHash, contentProvider, 1),
                        "Placeholder hydrated before invoking Explorer Free up space.",
                        "Placeholder did not hydrate correctly before Explorer Free up space.",
                        "expectedSha256=" + expectedHash
                        + ", actualSha256=" + hydratedHash
                        + ", attributes=" + FormatAttributes(File.GetAttributes(placeholderPath))
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                int downloadsBeforeVerb = contentProvider.DownloadCount;
                ExplorerVerbSmokeResult verbResult = await InvokeExplorerFreeUpSpaceForSmokeAsync(
                        context,
                        interactiveFolderSmoke,
                        folderPath,
                        placeholderPath)
                    .ConfigureAwait(false);
                failures += verbResult.Failures;

                if (!verbResult.Invoked)
                {
                    failures++;
                }
                else
                {
                    var dehydrationWork = new WindowsVirtualFilesDehydrationPairWork(
                        NoopSyncPairWork.Instance,
                        stateStore,
                        cloudFiles,
                        new LocalFileScanner(),
                        diagnostics);
                    await dehydrationWork
                        .RunOnceAsync(
                            syncPair,
                            SyncRunRequest.ForLocalChangedPaths(
                                interactiveFolderSmoke
                                    ? [relativeFolderPath, relativePlaceholderPath]
                                    : [relativePlaceholderPath]),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                        FormatCheck(true, "Production app Free up space handler processed the Explorer attribute change.")
                        + " path="
                        + relativePlaceholderPath)
                        .ConfigureAwait(false);
                }

                bool becameOnlineOnly = await WaitForAttributesAsync(
                    placeholderPath,
                    HasRecallOnDataAccess,
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                    .ConfigureAwait(false);
                FileAttributes dehydratedAttributes = File.GetAttributes(placeholderPath);
                failures += await WriteOutcomeAsync(
                        output,
                        becameOnlineOnly && contentProvider.DownloadCount == downloadsBeforeVerb,
                        "Explorer Free up space returned the file to online-only state without remote transfer.",
                        "Explorer Free up space did not return the file to online-only state cleanly.",
                        "attributes=" + FormatAttributes(dehydratedAttributes)
                        + ", downloadsBeforeVerb="
                        + downloadsBeforeVerb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterVerb="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                string rehydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string rehydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rehydratedText)));
                failures += await WriteOutcomeAsync(
                        output,
                        IsExpectedHydration(
                            rehydratedText,
                            rehydratedHash,
                            expectedText,
                            expectedHash,
                            contentProvider,
                            downloadsBeforeVerb + 1),
                        "Opening the Explorer-dehydrated placeholder hydrated unchanged remote content.",
                        "Explorer-dehydrated placeholder did not rehydrate unchanged remote content.",
                        "expectedSha256=" + expectedHash
                        + ", actualSha256=" + rehydratedHash
                        + ", downloadsBeforeVerb="
                        + downloadsBeforeVerb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterRehydrate="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
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

        private static bool IsExpectedHydration(
            string actualText,
            string actualHash,
            string expectedText,
            string expectedHash,
            StaticSmokeContentProvider contentProvider,
            int expectedDownloadCount)
        {
            return string.Equals(actualText, expectedText, StringComparison.Ordinal)
                && string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                && contentProvider.DownloadCount == expectedDownloadCount;
        }

        private static async Task<ExplorerVerbSmokeResult> InvokeExplorerFreeUpSpaceForSmokeAsync(
            WindowsVirtualFilesSmokeContext context,
            bool interactiveFolderSmoke,
            string folderPath,
            string placeholderPath)
        {
            if (interactiveFolderSmoke)
            {
                return await ObserveInteractiveFreeUpSpaceAsync(context, folderPath, placeholderPath)
                    .ConfigureAwait(false);
            }

            ShellVerbInvocationResult shellResult = await InvokeExplorerFreeUpSpaceAsync(
                    placeholderPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await context.Output.WriteLineAsync(
                    FormatCheck(shellResult.Invoked, "Explorer shell exposed and invoked the Free up space verb.")
                    + " verb=" + (shellResult.InvokedVerbName ?? "missing")
                    + ", availableVerbs=" + string.Join("|", shellResult.AvailableVerbNames))
                .ConfigureAwait(false);
            return new ExplorerVerbSmokeResult(shellResult.Invoked, shellResult.Invoked ? 0 : 1);
        }

        private static async Task<ExplorerVerbSmokeResult> ObserveInteractiveFreeUpSpaceAsync(
            WindowsVirtualFilesSmokeContext context,
            string folderPath,
            string placeholderPath)
        {
            IWindowsCloudFilesNativeApi nativeApi = context.NativeApi!;
            nativeApi.SetPinState(folderPath, WindowsCloudFilesPinState.Pinned);
            nativeApi.SetPinState(placeholderPath, WindowsCloudFilesPinState.Pinned);
            await WaitForAttributesAsync(
                    folderPath,
                    HasPinned,
                    TimeSpan.FromSeconds(10),
                    context.CancellationToken)
                .ConfigureAwait(false);
            await WaitForAttributesAsync(
                    placeholderPath,
                    HasPinned,
                    TimeSpan.FromSeconds(10),
                    context.CancellationToken)
                .ConfigureAwait(false);
            FileAttributes pinnedFolderAttributes = File.GetAttributes(folderPath);
            FileAttributes pinnedFileAttributes = File.GetAttributes(placeholderPath);
            bool ready = HasPinned(pinnedFolderAttributes) && HasPinned(pinnedFileAttributes);
            int failures = await WriteCheckAsync(
                    context.Output,
                    ready,
                    "Hydrated folder subtree is ready for modern Explorer Free up space.",
                    "folder=" + folderPath
                    + ", folderAttributes=" + FormatAttributes(pinnedFolderAttributes)
                    + ", fileAttributes=" + FormatAttributes(pinnedFileAttributes))
                .ConfigureAwait(false);
            TimeSpan holdDuration = context.StartupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder;
            await context.Output.WriteLineAsync(
                    "Holding hydrated folder for "
                    + holdDuration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds; invoke modern Explorer Free up space on "
                    + folderPath)
                .ConfigureAwait(false);
            await Task.Delay(holdDuration, context.CancellationToken).ConfigureAwait(false);
            FileAttributes folderAttributesAfterVerb = File.GetAttributes(folderPath);
            FileAttributes fileAttributesAfterVerb = File.GetAttributes(placeholderPath);
            bool invoked = HasUnpinned(folderAttributesAfterVerb) || HasUnpinned(fileAttributesAfterVerb);
            failures += await WriteCheckAsync(
                    context.Output,
                    invoked,
                    "Modern Explorer folder Free up space changed the subtree pin state.",
                    "folderAttributes=" + FormatAttributes(folderAttributesAfterVerb)
                    + ", fileAttributes=" + FormatAttributes(fileAttributesAfterVerb))
                .ConfigureAwait(false);
            return new ExplorerVerbSmokeResult(invoked, failures);
        }
}
}
