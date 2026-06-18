// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class DesktopWindowsVirtualFilesSmokeRunner
    {
        private const string DefaultSmokeRoot = @"S:\CottonSyncVfsQa\root";
        private const string AllowedSmokeRoot = @"S:\CottonSyncVfsQa";
        private const string RelativePlaceholderPath = "remote-only-smoke.txt";
        private const string LargeTreeDirectoryName = "large-tree";
        private const int LargeTreePlaceholderCount = 10_000;
        private const string LargeHydrationRelativePath = "large-hydration-smoke.bin";
        private const int LargeHydrationSizeBytes = 32 * 1024 * 1024;
        private const int LargeHydrationChunkBytes = 1024 * 1024;
        private const string SmokeContentText = "Cotton Sync Windows virtual files smoke content\n";

        public static async Task<int> RunAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            Func<string, CancellationToken, Task<string>>? readAllTextAsync = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            await output.WriteLineAsync("Cotton Sync Desktop Windows virtual files smoke").ConfigureAwait(false);
            await output.WriteLineAsync("Allowed destructive root: " + AllowedSmokeRoot + @"\...").ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                await output.WriteLineAsync(FormatCheck(false, "Windows Cloud Files API is only available on Windows."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = ResolveSmokeRoot(startupOptions.LocalRoot);
            string? rootError = ValidateSmokeRoot(rootPath);
            if (rootError is not null)
            {
                await output.WriteLineAsync(FormatCheck(false, rootError)).ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            var diagnostics = new WindowsCloudFilesDiagnostics();
            string phase = (startupOptions.WindowsVirtualFilesSmokePhase ?? string.Empty).Trim().ToLowerInvariant();
            bool leaveRegistered = string.Equals(phase, "leave-registered", StringComparison.Ordinal);
            bool reconnectExisting = string.Equals(phase, "reconnect-existing", StringComparison.Ordinal);
            bool largeTree = string.Equals(phase, "large-tree", StringComparison.Ordinal);
            bool largeHydration = string.Equals(phase, "large-hydration-progress", StringComparison.Ordinal);
            bool removePairCleanup = string.Equals(phase, "remove-pair-cleanup", StringComparison.Ordinal);
            bool trayQuitDisconnect = string.Equals(phase, "tray-quit-disconnect", StringComparison.Ordinal);
            bool explorerFreeUpSpace = string.Equals(phase, "explorer-free-up-space", StringComparison.Ordinal);
            bool remoteUpdateAfterDehydrate = string.Equals(phase, "remote-update-after-dehydrate", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(phase)
                && !leaveRegistered
                && !reconnectExisting
                && !largeTree
                && !largeHydration
                && !removePairCleanup
                && !trayQuitDisconnect
                && !explorerFreeUpSpace
                && !remoteUpdateAfterDehydrate)
            {
                await output.WriteLineAsync(FormatCheck(false, "Unsupported Windows virtual-files smoke phase: " + phase))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            IWindowsCloudFilesNativeApi? nativeApi = cloudFilesAdapter is null
                ? new WindowsCloudFilesNativeApi()
                : null;
            IWindowsCloudFilesAdapter cloudFiles = cloudFilesAdapter
                ?? new WindowsCloudFilesAdapter(nativeApi: nativeApi, diagnostics: diagnostics);
            SyncPairSettings syncPair = CreateSyncPair(rootPath);
            if (largeTree)
            {
                return await RunLargeTreeAsync(
                    startupOptions,
                    output,
                    cloudFiles,
                    syncPair,
                    diagnostics,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (removePairCleanup)
            {
                return await RunRemovePairCleanupAsync(
                    output,
                    cloudFiles,
                    nativeApi,
                    syncPair,
                    diagnostics,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (trayQuitDisconnect)
            {
                return await RunTrayQuitDisconnectAsync(
                    paths,
                    output,
                    cloudFiles,
                    nativeApi,
                    syncPair,
                    diagnostics,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (explorerFreeUpSpace)
            {
                return await RunExplorerFreeUpSpaceAsync(
                    paths,
                    output,
                    cloudFiles,
                    nativeApi,
                    syncPair,
                    diagnostics,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (largeHydration)
            {
                return await RunLargeHydrationAsync(
                    paths,
                    output,
                    cloudFiles,
                    nativeApi,
                    syncPair,
                    diagnostics,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedText = Encoding.UTF8.GetString(expectedContent);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            RemoteFilePlaceholderRequest placeholderRequest = CreatePlaceholderRequest(
                syncPair,
                RelativePlaceholderPath,
                expectedContent.LongLength,
                expectedHash);
            var contentProvider = new StaticSmokeContentProvider(expectedContent);
            IWindowsCloudFilesCallbackHandler callbackHandler = nativeApi is null
                ? new NoopCloudFilesCallbackHandler()
                : new WindowsCloudFilesHydrationCoordinator(
                    contentProvider,
                    nativeApi,
                    Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                    diagnostics);
            Func<string, CancellationToken, Task<string>> reader =
                readAllTextAsync ?? ReadAllTextThroughExternalProcessAsync;
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
                if (!reconnectExisting)
                {
                    TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                    PrepareRoot(rootPath);
                    await output.WriteLineAsync(FormatCheck(true, "Isolated QA root prepared.") + " root=" + rootPath)
                        .ConfigureAwait(false);

                    RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(placeholderRequest);
                    if (contentProvider.DownloadCount == 0)
                    {
                        await output.WriteLineAsync(
                            FormatCheck(true, "Placeholder creation did not download remote content.")
                            + " identityBytes=" + (placeholder.PlaceholderIdentity?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        failures++;
                        await output.WriteLineAsync(FormatCheck(false, "Placeholder creation unexpectedly downloaded remote content."))
                            .ConfigureAwait(false);
                    }
                }

                if (File.Exists(placeholderPath))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, reconnectExisting
                            ? "Existing remote-only placeholder is available before reconnect hydration."
                            : "Remote-only placeholder exists before hydration.")
                        + " path=" + placeholderPath
                        + ", attributes=" + FormatAttributes(File.GetAttributes(placeholderPath))
                        + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(FormatCheck(false, "Remote-only placeholder file was not created."))
                        .ConfigureAwait(false);
                }

                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected only under the isolated QA root.")
                    + " root=" + connection.LocalRootPath)
                    .ConfigureAwait(false);

                if (startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder > TimeSpan.Zero)
                {
                    await output.WriteLineAsync(
                        "Holding after remote-only placeholder creation for "
                        + startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder.TotalSeconds.ToString(
                            "0.###",
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds; inspect "
                        + placeholderPath
                        + " before hydration starts.")
                        .ConfigureAwait(false);
                    await Task
                        .Delay(startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (leaveRegistered)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Cloud Files sync root left registered for process restart smoke.")
                        + " root=" + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    string hydratedText = await reader(placeholderPath, cancellationToken).ConfigureAwait(false);
                    byte[] hydratedBytes = Encoding.UTF8.GetBytes(hydratedText);
                    string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(hydratedBytes));
                    if (string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                        && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await output.WriteLineAsync(
                            FormatCheck(true, "Opening the placeholder hydrated exact remote content.")
                            + " sha256=" + hydratedHash
                            + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        failures++;
                        await output.WriteLineAsync(
                            FormatCheck(false, "Hydrated content did not match expected remote content.")
                            + " expectedSha256=" + expectedHash
                            + ", actualSha256=" + hydratedHash)
                            .ConfigureAwait(false);
                    }

                    if (nativeApi is not null && contentProvider.DownloadCount == 0)
                    {
                        failures++;
                        await output.WriteLineAsync(FormatCheck(false, "Opening the placeholder did not trigger a Cloud Files fetch callback."))
                            .ConfigureAwait(false);
                    }

                    if (nativeApi is not null)
                    {
                        int downloadsBeforeDehydrate = contentProvider.DownloadCount;
                        nativeApi.DehydratePlaceholder(placeholderPath);
                        FileAttributes dehydratedAttributes = File.GetAttributes(placeholderPath);
                        if (HasRecallOnDataAccess(dehydratedAttributes)
                            && contentProvider.DownloadCount == downloadsBeforeDehydrate)
                        {
                            await output.WriteLineAsync(
                                FormatCheck(true, "Dehydrating the hydrated placeholder freed local content without remote transfer.")
                                + " attributes=" + FormatAttributes(dehydratedAttributes)
                                + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            failures++;
                            await output.WriteLineAsync(
                                FormatCheck(false, "Dehydrating the hydrated placeholder did not return it to online-only state.")
                                + " attributes=" + FormatAttributes(dehydratedAttributes)
                                + ", downloadsBefore="
                                + downloadsBeforeDehydrate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + ", downloadsAfter="
                                + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }

                        if (remoteUpdateAfterDehydrate)
                        {
                            byte[] updatedContent = Encoding.UTF8.GetBytes(
                                "Cotton Sync Windows virtual files updated smoke content\n");
                            string updatedText = Encoding.UTF8.GetString(updatedContent);
                            string updatedHash = Convert.ToHexStringLower(SHA256.HashData(updatedContent));
                            int downloadsBeforeUpdate = contentProvider.DownloadCount;
                            cloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                                syncPair,
                                RelativePlaceholderPath,
                                updatedContent.LongLength,
                                updatedHash));
                            contentProvider.SetContent(updatedContent);

                            var updatedInfo = new FileInfo(placeholderPath);
                            FileAttributes updatedAttributes = updatedInfo.Attributes;
                            if (updatedInfo.Length == updatedContent.LongLength
                                && HasRecallOnDataAccess(updatedAttributes)
                                && contentProvider.DownloadCount == downloadsBeforeUpdate)
                            {
                                await output.WriteLineAsync(
                                    FormatCheck(true, "Remote update after dehydration refreshed placeholder metadata without downloading content.")
                                    + " sizeBytes="
                                    + updatedInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", attributes="
                                    + FormatAttributes(updatedAttributes)
                                    + ", downloads="
                                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                failures++;
                                await output.WriteLineAsync(
                                    FormatCheck(false, "Remote update after dehydration did not refresh placeholder metadata correctly.")
                                    + " expectedSizeBytes="
                                    + updatedContent.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", actualSizeBytes="
                                    + updatedInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", attributes="
                                    + FormatAttributes(updatedAttributes)
                                    + ", downloadsBeforeUpdate="
                                    + downloadsBeforeUpdate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", downloadsAfterUpdate="
                                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }

                            string updatedHydratedText = await reader(placeholderPath, cancellationToken).ConfigureAwait(false);
                            string updatedHydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(updatedHydratedText)));
                            if (string.Equals(updatedHydratedText, updatedText, StringComparison.Ordinal)
                                && string.Equals(updatedHydratedHash, updatedHash, StringComparison.OrdinalIgnoreCase)
                                && contentProvider.DownloadCount == downloadsBeforeUpdate + 1)
                            {
                                await output.WriteLineAsync(
                                    FormatCheck(true, "Opening the updated dehydrated placeholder hydrated the latest remote content.")
                                    + " sha256="
                                    + updatedHydratedHash
                                    + ", downloads="
                                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                failures++;
                                await output.WriteLineAsync(
                                    FormatCheck(false, "Opening the updated dehydrated placeholder did not hydrate the latest remote content.")
                                    + " expectedSha256="
                                    + updatedHash
                                    + ", actualSha256="
                                    + updatedHydratedHash
                                    + ", downloadsBeforeUpdate="
                                    + downloadsBeforeUpdate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", downloadsAfterHydration="
                                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            connection.Dispose();
                            connection = null;
                            int downloadsBeforeReconnect = contentProvider.DownloadCount;
                            await output.WriteLineAsync("Disconnected Cloud Files sync root before reconnect smoke.").ConfigureAwait(false);

                            connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                            await output.WriteLineAsync(
                                FormatCheck(true, "Cloud Files sync root reconnected after provider restart simulation.")
                                + " root=" + connection.LocalRootPath)
                                .ConfigureAwait(false);

                            string rehydratedText = await reader(placeholderPath, cancellationToken).ConfigureAwait(false);
                            string rehydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rehydratedText)));
                            if (string.Equals(rehydratedText, expectedText, StringComparison.Ordinal)
                                && string.Equals(rehydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                                && contentProvider.DownloadCount == downloadsBeforeReconnect + 1)
                            {
                                await output.WriteLineAsync(
                                    FormatCheck(true, "Reconnected Cloud Files callbacks hydrated the placeholder without duplicate registration.")
                                    + " sha256=" + rehydratedHash
                                    + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                failures++;
                                await output.WriteLineAsync(
                                    FormatCheck(false, "Reconnected Cloud Files callbacks did not hydrate the placeholder correctly.")
                                    + " expectedSha256=" + expectedHash
                                    + ", actualSha256=" + rehydratedHash
                                    + ", downloadsBeforeReconnect="
                                    + downloadsBeforeReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + ", downloadsAfterReconnect="
                                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                if (!leaveRegistered)
                {
                    failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
                }
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

        private static async Task<int> RunExplorerFreeUpSpaceAsync(
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
                await output.WriteLineAsync(FormatCheck(false, "Explorer Free up space smoke requires the native Windows Cloud Files API."))
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
                    FormatCheck(true, "Cloud Files callbacks connected for Explorer Free up space smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                string hydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
                if (string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                    && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                    && contentProvider.DownloadCount == 1)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Placeholder hydrated before invoking Explorer Free up space.")
                        + " sha256="
                        + hydratedHash
                        + ", attributes="
                        + FormatAttributes(File.GetAttributes(placeholderPath))
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Placeholder did not hydrate correctly before Explorer Free up space.")
                        + " expectedSha256="
                        + expectedHash
                        + ", actualSha256="
                        + hydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }

                int downloadsBeforeVerb = contentProvider.DownloadCount;
                ShellVerbInvocationResult verbResult = await InvokeExplorerFreeUpSpaceAsync(
                    placeholderPath,
                    cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(verbResult.Invoked, "Explorer shell exposed and invoked the Free up space verb.")
                    + " verb="
                    + (verbResult.InvokedVerbName ?? "missing")
                    + ", availableVerbs="
                    + string.Join("|", verbResult.AvailableVerbNames))
                    .ConfigureAwait(false);
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
                            SyncRunRequest.ForLocalChangedPaths([RelativePlaceholderPath]),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                        FormatCheck(true, "Production app Free up space handler processed the Explorer attribute change.")
                        + " path="
                        + RelativePlaceholderPath)
                        .ConfigureAwait(false);
                }

                bool becameOnlineOnly = await WaitForAttributesAsync(
                    placeholderPath,
                    HasRecallOnDataAccess,
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                    .ConfigureAwait(false);
                FileAttributes dehydratedAttributes = File.GetAttributes(placeholderPath);
                if (becameOnlineOnly && contentProvider.DownloadCount == downloadsBeforeVerb)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Explorer Free up space returned the file to online-only state without remote transfer.")
                        + " attributes="
                        + FormatAttributes(dehydratedAttributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Explorer Free up space did not return the file to online-only state cleanly.")
                        + " attributes="
                        + FormatAttributes(dehydratedAttributes)
                        + ", downloadsBeforeVerb="
                        + downloadsBeforeVerb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterVerb="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }

                string rehydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string rehydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rehydratedText)));
                if (string.Equals(rehydratedText, expectedText, StringComparison.Ordinal)
                    && string.Equals(rehydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                    && contentProvider.DownloadCount == downloadsBeforeVerb + 1)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Opening the Explorer-dehydrated placeholder hydrated unchanged remote content.")
                        + " sha256="
                        + rehydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Explorer-dehydrated placeholder did not rehydrate unchanged remote content.")
                        + " expectedSha256="
                        + expectedHash
                        + ", actualSha256="
                        + rehydratedHash
                        + ", downloadsBeforeVerb="
                        + downloadsBeforeVerb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterRehydrate="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
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

        private static SyncStateEntry CreatePlaceholderState(
            SyncPairSettings syncPair,
            RemoteFilePlaceholderRequest request,
            RemoteFilePlaceholderResult placeholder)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = Cotton.Sync.State.SyncPath.Normalize(request.RelativePath),
                Kind = SyncEntryKind.File,
                RemoteSizeBytes = request.RemoteFile.SizeBytes,
                RemoteFileId = request.RemoteFile.Id,
                RemoteNodeId = request.RemoteFile.NodeId,
                RemoteContentHash = request.RemoteFile.ContentHash,
                RemoteETag = request.RemoteFile.ETag,
                PlaceholderIdentity = placeholder.PlaceholderIdentity,
                PlaceholderHydrationState = placeholder.HydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

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
                if (File.Exists(placeholderPath) && HasRecallOnDataAccess(stoppedAttributes))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Tray quit lifecycle stop disconnected callbacks without corrupting the placeholder.")
                        + " attributes="
                        + FormatAttributes(stoppedAttributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Placeholder was missing or lost online-only state after tray quit lifecycle stop.")
                        + " exists="
                        + File.Exists(placeholderPath).ToString()
                        + ", attributes="
                        + (File.Exists(placeholderPath) ? FormatAttributes(stoppedAttributes) : "missing"))
                        .ConfigureAwait(false);
                }

                int downloadsBeforeReconnect = contentProvider.DownloadCount;
                await connectionCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files callbacks reconnected after tray quit simulation."))
                    .ConfigureAwait(false);

                string hydratedText = await ReadAllTextThroughExternalProcessAsync(placeholderPath, cancellationToken)
                    .ConfigureAwait(false);
                string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
                if (string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                    && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                    && contentProvider.DownloadCount == downloadsBeforeReconnect + 1)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Reconnected callbacks hydrated exact remote content after tray quit simulation.")
                        + " sha256="
                        + hydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Reconnected callbacks did not hydrate exact content after tray quit simulation.")
                        + " expectedSha256="
                        + expectedHash
                        + ", actualSha256="
                        + hydratedHash
                        + ", downloadsBeforeReconnect="
                        + downloadsBeforeReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", downloadsAfterReconnect="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await connectionCoordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Final lifecycle disconnect failed: " + CleanSingleLine(exception.Message)))
                        .ConfigureAwait(false);
                }

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

        private static async Task<int> RunRemovePairCleanupAsync(
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
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for remove-pair cleanup smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                cloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                    syncPair,
                    RelativePlaceholderPath,
                    expectedContent.LongLength,
                    expectedHash));
                await output.WriteLineAsync(
                    FormatCheck(true, "Registered Cloud Files root and placeholder before pair removal.")
                    + " path="
                    + placeholderPath
                    + ", attributes="
                    + FormatAttributes(File.GetAttributes(placeholderPath)))
                    .ConfigureAwait(false);

                var deletionHandler = new WindowsCloudFilesSyncPairDeletionHandler(cloudFiles);
                await deletionHandler.BeforeDeleteAsync(syncPair, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Removing the virtual-files sync pair unregistered the Cloud Files root.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                Directory.Delete(rootPath, recursive: true);
                if (!Directory.Exists(rootPath))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Unregistered sync root could be removed from the filesystem cleanly.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(
                        FormatCheck(false, "Unregistered sync root still exists after filesystem cleanup.")
                        + " root="
                        + rootPath)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
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
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
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
                Directory.CreateDirectory(largeTreePath);
                await output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared for large-tree Explorer smoke.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                var createTimer = Stopwatch.StartNew();
                for (int index = 0; index < LargeTreePlaceholderCount; index++)
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
                            + LargeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                            + " placeholders.")
                            .ConfigureAwait(false);
                    }
                }

                createTimer.Stop();
                await output.WriteLineAsync(
                    FormatCheck(true, "Large remote-only placeholder tree was created.")
                    + " files="
                    + LargeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + ", elapsedMs="
                    + createTimer.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                connection = cloudFiles.ConnectSyncRoot(syncPair, new NoopCloudFilesCallbackHandler());
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected for large-tree Explorer smoke.")
                    + " root="
                    + connection.LocalRootPath)
                    .ConfigureAwait(false);

                var enumerationTimer = Stopwatch.StartNew();
                int enumeratedFiles = Directory.EnumerateFiles(largeTreePath, "*.txt", SearchOption.TopDirectoryOnly).Count();
                enumerationTimer.Stop();
                if (enumeratedFiles == LargeTreePlaceholderCount)
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
                        + LargeTreePlaceholderCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
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
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
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

        private static string ResolveSmokeRoot(string? configuredRoot)
        {
            return string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.GetFullPath(DefaultSmokeRoot)
                : Path.GetFullPath(configuredRoot.Trim());
        }

        private static string? ValidateSmokeRoot(string rootPath)
        {
            if (!Path.IsPathFullyQualified(rootPath))
            {
                return "Windows virtual-files smoke root must be an absolute path under " + AllowedSmokeRoot + @"\...";
            }

            string allowedRoot = Path.GetFullPath(AllowedSmokeRoot);
            StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            string normalizedRoot = NormalizeFullPath(rootPath);
            string normalizedAllowed = NormalizeFullPath(allowedRoot);
            if (string.Equals(normalizedRoot, normalizedAllowed, comparison)
                || !normalizedRoot.StartsWith(EnsureTrailingSeparator(normalizedAllowed), comparison))
            {
                return "Windows virtual-files smoke refuses to touch paths outside " + AllowedSmokeRoot + @"\...";
            }

            return null;
        }

        private static void PrepareRoot(string rootPath)
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            Directory.CreateDirectory(rootPath);
        }

        private static void TryUnregisterExistingRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine("Info: previous Cloud Files registration was unregistered before smoke.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine("Info: no previous Cloud Files registration cleanup was confirmed: " + CleanSingleLine(exception.Message));
            }
        }

        private static int TryUnregisterSmokeRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine(FormatCheck(true, "Cloud Files sync root unregistered after smoke.") + " root=" + syncPair.LocalRootPath);
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine(
                    FormatCheck(false, "Cloud Files sync root cleanup failed.")
                    + " "
                    + CleanSingleLine(exception.Message));
                return 1;
            }
        }

        private static SyncPairSettings CreateSyncPair(string rootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Cotton Sync VFS smoke",
                LocalRootPath = rootPath,
                RemoteDisplayPath = "/CottonSyncQa/WindowsVirtualFilesSmoke",
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static RemoteFilePlaceholderRequest CreatePlaceholderRequest(
            SyncPairSettings syncPair,
            string relativePath,
            long sizeBytes,
            string contentHash)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = sizeBytes,
                    ContentHash = contentHash,
                    ETag = "vfs-smoke-etag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string CleanSingleLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Operation could not be completed.";
            }

            return message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static string FormatAttributes(FileAttributes attributes)
        {
            const int RecallOnOpen = 0x00040000;
            const int Pinned = 0x00080000;
            const int Unpinned = 0x00100000;
            const int RecallOnDataAccess = 0x00400000;

            var names = new List<string>();
            int raw = (int)attributes;
            foreach (FileAttributes known in Enum.GetValues<FileAttributes>())
            {
                if ((int)known == 0 || known == FileAttributes.Normal)
                {
                    continue;
                }

                if ((attributes & known) == known)
                {
                    names.Add(known.ToString());
                    raw &= ~(int)known;
                }
            }

            AddKnownCloudFilesAttribute(raw, RecallOnOpen, "RecallOnOpen", names, out raw);
            AddKnownCloudFilesAttribute(raw, Pinned, "Pinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, Unpinned, "Unpinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, RecallOnDataAccess, "RecallOnDataAccess", names, out raw);
            if (names.Count == 0)
            {
                names.Add(FileAttributes.Normal.ToString());
            }

            if (raw != 0)
            {
                names.Add("0x" + raw.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join("|", names)
                + " (raw=0x"
                + ((int)attributes).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + ")";
        }

        private static bool HasRecallOnDataAccess(FileAttributes attributes)
        {
            const int RecallOnDataAccess = 0x00400000;
            return (((int)attributes) & RecallOnDataAccess) == RecallOnDataAccess;
        }

        private static void AddKnownCloudFilesAttribute(
            int raw,
            int flag,
            string name,
            List<string> names,
            out int remaining)
        {
            remaining = raw;
            if ((raw & flag) == flag)
            {
                names.Add(name);
                remaining &= ~flag;
            }
        }

        private static byte[] CreateLargeHydrationContent()
        {
            byte[] content = new byte[LargeHydrationSizeBytes];
            for (int index = 0; index < content.Length; index++)
            {
                content[index] = (byte)((index * 31 + index / 8191) & 0xff);
            }

            return content;
        }

        private static async Task<string> ReadAllTextThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadAllBytesThroughExternalProcessAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        private static async Task<byte[]> ReadAllBytesThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            string base64 = await RunPowerShellFileReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$bytes=[System.IO.File]::ReadAllBytes($env:COTTON_SYNC_EXTERNAL_READ_PATH); "
                + "[Convert]::ToBase64String($bytes)",
                filePath,
                cancellationToken)
                .ConfigureAwait(false);
            return Convert.FromBase64String(base64.Trim());
        }

        private static async Task<FileContentHash> ReadFileHashThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await using FileStream stream = File.OpenRead(filePath);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return new FileContentHash(stream.Length, Convert.ToHexStringLower(hash));
            }

            string output = await RunPowerShellFileReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$stream=[System.IO.File]::OpenRead($env:COTTON_SYNC_EXTERNAL_READ_PATH); "
                + "try { "
                + "$sha=[System.Security.Cryptography.SHA256]::Create(); "
                + "$hash=$sha.ComputeHash($stream); "
                + "$hex=([System.BitConverter]::ToString($hash)).Replace('-','').ToLowerInvariant(); "
                + "'{0}|{1}' -f $stream.Length,$hex "
                + "} finally { $stream.Dispose(); if ($sha) { $sha.Dispose(); } }",
                filePath,
                cancellationToken)
                .ConfigureAwait(false);
            string[] parts = output.Trim().Split('|', 2);
            if (parts.Length != 2
                || !long.TryParse(
                    parts[0],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long length))
            {
                throw new InvalidOperationException("External file hash helper returned an invalid response.");
            }

            return new FileContentHash(length, parts[1]);
        }

        private static async Task<string> RunPowerShellFileReadAsync(
            string script,
            string filePath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["COTTON_SYNC_EXTERNAL_READ_PATH"] = filePath;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the external file-read helper process.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    "External file-read helper failed with exit code "
                    + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": "
                    + CleanSingleLine(error));
            }

            return output;
        }

        private static bool HasIntermediateProgress(IReadOnlyList<SyncTransferProgress> progress)
        {
            return progress.Any(static item =>
                !item.IsCompleted
                && item.TotalBytes.HasValue
                && item.TransferredBytes > 0
                && item.TransferredBytes < item.TotalBytes.Value);
        }

        private static bool IsMonotonicProgress(IReadOnlyList<SyncTransferProgress> progress)
        {
            long previous = -1;
            foreach (SyncTransferProgress item in progress.Where(static value => value.Direction == SyncTransferDirection.Download))
            {
                if (item.TransferredBytes < previous)
                {
                    return false;
                }

                previous = item.TransferredBytes;
            }

            return true;
        }

        private static async Task<bool> WaitForAttributesAsync(
            string filePath,
            Func<FileAttributes, bool> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(filePath) && predicate(File.GetAttributes(filePath)))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return File.Exists(filePath) && predicate(File.GetAttributes(filePath));
        }

        private static Task<ShellVerbInvocationResult> InvokeExplorerFreeUpSpaceAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<ShellVerbInvocationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(InvokeExplorerFreeUpSpaceCore(filePath));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.IsBackground = true;
            thread.Start();
            cancellationToken.Register(
                static state => ((TaskCompletionSource<ShellVerbInvocationResult>)state!).TrySetCanceled(),
                completion);
            return completion.Task;
        }

        private static ShellVerbInvocationResult InvokeExplorerFreeUpSpaceCore(string filePath)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new ShellVerbInvocationResult(false, null, []);
            }

            string? directory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return new ShellVerbInvocationResult(false, null, []);
            }

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return new ShellVerbInvocationResult(false, null, []);
            }

            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Shell.Application COM object could not be created.");
            dynamic folder = shell.Namespace(directory);
            if (folder is null)
            {
                return new ShellVerbInvocationResult(false, null, []);
            }

            dynamic item = folder.ParseName(fileName);
            if (item is null)
            {
                return new ShellVerbInvocationResult(false, null, []);
            }

            var names = new List<string>();
            dynamic verbs = item.Verbs();
            int count = verbs.Count;
            for (int index = 0; index < count; index++)
            {
                dynamic verb = verbs.Item(index);
                string name = CleanShellVerbName((string)verb.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }

                if (IsFreeUpSpaceVerb(name))
                {
                    verb.DoIt();
                    return new ShellVerbInvocationResult(true, name, names);
                }
            }

            return new ShellVerbInvocationResult(false, null, names);
        }

        private static string CleanShellVerbName(string? value)
        {
            return (value ?? string.Empty)
                .Replace("&", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private static bool IsFreeUpSpaceVerb(string value)
        {
            return value.Contains("Free up space", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u044c \u043c\u0435\u0441\u0442\u043e", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record ShellVerbInvocationResult(
            bool Invoked,
            string? InvokedVerbName,
            IReadOnlyList<string> AvailableVerbNames);

        private sealed class RecordingTransferProgress : IProgress<SyncTransferProgress>
        {
            private readonly object _gate = new();
            private readonly List<SyncTransferProgress> _values = [];

            public void Report(SyncTransferProgress value)
            {
                lock (_gate)
                {
                    _values.Add(value);
                }
            }

            public IReadOnlyList<SyncTransferProgress> Snapshot()
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }

            public void Clear()
            {
                lock (_gate)
                {
                    _values.Clear();
                }
            }

            public async Task<bool> WaitForSampleCountAsync(int count, TimeSpan timeout)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < timeout)
                {
                    lock (_gate)
                    {
                        if (_values.Count >= count)
                        {
                            return true;
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
                }

                return false;
            }
        }

        private sealed class ChunkedSmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;
            private readonly int _chunkSize;
            private TimeSpan _chunkDelay;

            public ChunkedSmokeContentProvider(byte[] content, int chunkSize, TimeSpan chunkDelay)
            {
                ArgumentNullException.ThrowIfNull(content);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
                _content = content;
                _chunkSize = chunkSize;
                _chunkDelay = chunkDelay;
            }

            public int DownloadCount { get; private set; }

            public int CancellationCount { get; private set; }

            public void ResetCancellation()
            {
                CancellationCount = 0;
            }

            public void SetChunkDelay(TimeSpan chunkDelay)
            {
                _chunkDelay = chunkDelay;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                DownloadCount++;
                long transferred = 0;
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    _content.LongLength,
                    isCompleted: false));

                try
                {
                    while (transferred < _content.LongLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int length = (int)Math.Min(_chunkSize, _content.LongLength - transferred);
                        await destination
                            .WriteAsync(_content.AsMemory((int)transferred, length), cancellationToken)
                            .ConfigureAwait(false);
                        transferred += length;
                        transferProgress?.Report(new SyncTransferProgress(
                            SyncTransferDirection.Download,
                            identity.RelativePath,
                            transferred,
                            _content.LongLength,
                            isCompleted: transferred == _content.LongLength));
                        if (_chunkDelay > TimeSpan.Zero && transferred < _content.LongLength)
                        {
                            await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    destination.Position = 0;
                }
                catch (OperationCanceledException)
                {
                    CancellationCount++;
                    throw;
                }
            }
        }

        private sealed class RecordingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            private readonly IWindowsCloudFilesCallbackHandler _inner;

            public RecordingCallbackHandler(IWindowsCloudFilesCallbackHandler inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public int CancelFetchDataCount { get; private set; }

            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.HandleFetchDataAsync(request, cancellationToken);
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
                CancelFetchDataCount++;
                _inner.CancelFetchData(request);
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.HandleDehydrateAsync(request, cancellationToken);
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
                _inner.NotifyDehydrateCompleted(notification);
            }
        }

        private sealed class NoopSyncPairWork : ISyncPairWork
        {
            public static NoopSyncPairWork Instance { get; } = new();

            private NoopSyncPairWork()
            {
            }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                throw new NotSupportedException();
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                throw new NotSupportedException();
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(string filePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                throw new NotSupportedException();
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer);
            }

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class StaticSmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private byte[] _content;

            public StaticSmokeContentProvider(byte[] content)
            {
                _content = content;
            }

            public int DownloadCount { get; private set; }

            public void SetContent(byte[] content)
            {
                ArgumentNullException.ThrowIfNull(content);
                _content = content;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                DownloadCount++;
                byte[] content = _content;
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    content.LongLength,
                    isCompleted: false));
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    content.LongLength,
                    content.LongLength,
                    isCompleted: true));
                destination.Position = 0;
            }
        }

        private sealed record FileContentHash(long Length, string Sha256);

        private sealed class NoopCloudFilesCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }

        private sealed class SingleSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private SyncPairSettings? _syncPair;

            public SingleSyncPairSettingsStore(SyncPairSettings syncPair)
            {
                _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<SyncPairSettings> result = _syncPair is null ? [] : [_syncPair];
                return Task.FromResult(result);
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncPairSettings? result = _syncPair is not null && _syncPair.Id == syncPairId
                    ? _syncPair
                    : null;
                return Task.FromResult(result);
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
                return Task.CompletedTask;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_syncPair is not null && _syncPair.Id == syncPairId)
                {
                    _syncPair = null;
                }

                return Task.CompletedTask;
            }
        }
    }
}
