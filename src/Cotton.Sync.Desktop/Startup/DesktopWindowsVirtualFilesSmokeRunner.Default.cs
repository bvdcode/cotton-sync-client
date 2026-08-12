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
        private static async Task<int> RunDefaultWindowsVirtualFilesSmokeAsync(
            WindowsVirtualFilesSmokeContext context)
        {
            DesktopAppPaths paths = context.Paths;
            TextWriter output = context.Output;
            IWindowsCloudFilesAdapter cloudFiles = context.CloudFiles;
            IWindowsCloudFilesNativeApi? nativeApi = context.NativeApi;
            SyncPairSettings syncPair = context.SyncPair;
            WindowsCloudFilesDiagnostics diagnostics = context.Diagnostics;
            string rootPath = syncPair.LocalRootPath;
            bool leaveRegistered = context.Phase == WindowsVirtualFilesSmokePhase.LeaveRegistered;
            bool reconnectExisting = context.Phase == WindowsVirtualFilesSmokePhase.ReconnectExisting;
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
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
                if (!reconnectExisting)
                {
                    failures += await PrepareDefaultPlaceholderAsync(
                            context,
                            placeholderRequest,
                            contentProvider)
                        .ConfigureAwait(false);
                }

                failures += await VerifyDefaultPlaceholderExistsAsync(
                        context,
                        contentProvider,
                        placeholderPath,
                        reconnectExisting)
                    .ConfigureAwait(false);

                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected only under the isolated QA root.")
                    + " root=" + connection.LocalRootPath)
                    .ConfigureAwait(false);

                await HoldDefaultPlaceholderAsync(context, placeholderPath).ConfigureAwait(false);

                if (leaveRegistered)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Cloud Files sync root left registered for process restart smoke.")
                        + " root=" + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    failures += await VerifyDefaultHydratedContentAsync(
                            context,
                            contentProvider,
                            placeholderPath,
                            expectedText,
                            expectedHash)
                        .ConfigureAwait(false);

                    if (nativeApi is not null)
                    {
                        DefaultVirtualFilesHydrationResult nativeResult = await RunDefaultNativeLifecycleAsync(
                                context,
                                contentProvider,
                                callbackHandler,
                                connection,
                                placeholderPath,
                                expectedText,
                                expectedHash)
                            .ConfigureAwait(false);
                        failures += nativeResult.Failures;
                        connection = nativeResult.Connection;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                if (!leaveRegistered)
                {
                    failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
                }
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task<int> PrepareDefaultPlaceholderAsync(
            WindowsVirtualFilesSmokeContext context,
            RemoteFilePlaceholderRequest placeholderRequest,
            StaticSmokeContentProvider contentProvider)
        {
            TryUnregisterExistingRoot(context.CloudFiles, context.SyncPair, context.Output);
            PrepareRoot(context.SyncPair.LocalRootPath);
            await context.Output.WriteLineAsync(
                    FormatCheck(true, "Isolated QA root prepared.") + " root=" + context.SyncPair.LocalRootPath)
                .ConfigureAwait(false);
            RemoteFilePlaceholderResult placeholder = context.CloudFiles.CreateFilePlaceholder(placeholderRequest);
            if (contentProvider.DownloadCount == 0)
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(true, "Placeholder creation did not download remote content.")
                        + " identityBytes="
                        + (placeholder.PlaceholderIdentity?.Length ?? 0).ToString(
                            System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await context.Output.WriteLineAsync(
                    FormatCheck(false, "Placeholder creation unexpectedly downloaded remote content."))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> VerifyDefaultPlaceholderExistsAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath,
            bool reconnectExisting)
        {
            if (!File.Exists(placeholderPath))
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(false, "Remote-only placeholder file was not created."))
                    .ConfigureAwait(false);
                return 1;
            }

            string message = reconnectExisting
                ? "Existing remote-only placeholder is available before reconnect hydration."
                : "Remote-only placeholder exists before hydration.";
            await context.Output.WriteLineAsync(
                    FormatCheck(true, message)
                    + " path=" + placeholderPath
                    + ", attributes=" + FormatAttributes(File.GetAttributes(placeholderPath))
                    + ", downloads="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 0;
        }

        private static async Task HoldDefaultPlaceholderAsync(
            WindowsVirtualFilesSmokeContext context,
            string placeholderPath)
        {
            TimeSpan holdDuration = context.StartupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder;
            if (holdDuration <= TimeSpan.Zero)
            {
                return;
            }

            await context.Output.WriteLineAsync(
                    "Holding after remote-only placeholder creation for "
                    + holdDuration.TotalSeconds.ToString(
                        "0.###",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds; inspect "
                    + placeholderPath
                    + " before hydration starts.")
                .ConfigureAwait(false);
            await Task.Delay(holdDuration, context.CancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> VerifyDefaultHydratedContentAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath,
            string expectedText,
            string expectedHash)
        {
            string hydratedText = await context.ReadAllTextAsync(placeholderPath, context.CancellationToken)
                .ConfigureAwait(false);
            string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
            bool contentMatches = string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
            if (contentMatches)
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(true, "Opening the placeholder hydrated exact remote content.")
                        + " sha256=" + hydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await context.Output.WriteLineAsync(
                    FormatCheck(false, "Hydrated content did not match expected remote content.")
                    + " expectedSha256=" + expectedHash
                    + ", actualSha256=" + hydratedHash)
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<DefaultVirtualFilesHydrationResult> RunDefaultNativeLifecycleAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            IWindowsCloudFilesCallbackHandler callbackHandler,
            WindowsCloudFilesConnection connection,
            string placeholderPath,
            string expectedText,
            string expectedHash)
        {
            int failures = await VerifyDefaultFetchCallbackAsync(context.Output, contentProvider).ConfigureAwait(false);
            failures += await DehydrateDefaultPlaceholderAsync(context, contentProvider, placeholderPath)
                .ConfigureAwait(false);
            if (context.Phase == WindowsVirtualFilesSmokePhase.RemoteUpdateAfterDehydrate)
            {
                failures += await RunRemoteUpdateAfterDehydrateAsync(context, contentProvider, placeholderPath)
                    .ConfigureAwait(false);
                return new DefaultVirtualFilesHydrationResult(failures, connection);
            }

            connection.Dispose();
            DefaultVirtualFilesHydrationResult reconnectResult = await ReconnectDefaultPlaceholderAsync(
                    context,
                    contentProvider,
                    callbackHandler,
                    placeholderPath,
                    expectedText,
                    expectedHash)
                .ConfigureAwait(false);
            return new DefaultVirtualFilesHydrationResult(
                failures + reconnectResult.Failures,
                reconnectResult.Connection);
        }

        private static async Task<int> VerifyDefaultFetchCallbackAsync(
            TextWriter output,
            StaticSmokeContentProvider contentProvider)
        {
            if (contentProvider.DownloadCount > 0)
            {
                return 0;
            }

            await output.WriteLineAsync(
                    FormatCheck(false, "Opening the placeholder did not trigger a Cloud Files fetch callback."))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> DehydrateDefaultPlaceholderAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath)
        {
            int downloadsBeforeDehydrate = contentProvider.DownloadCount;
            context.NativeApi!.DehydratePlaceholder(placeholderPath);
            FileAttributes attributes = File.GetAttributes(placeholderPath);
            bool passed = HasRecallOnDataAccess(attributes)
                && contentProvider.DownloadCount == downloadsBeforeDehydrate;
            string details = " attributes=" + FormatAttributes(attributes)
                + ", downloadsBefore="
                + downloadsBeforeDehydrate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", downloadsAfter="
                + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (passed)
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(true, "Dehydrating the hydrated placeholder freed local content without remote transfer.")
                        + " attributes=" + FormatAttributes(attributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await context.Output.WriteLineAsync(
                    FormatCheck(false, "Dehydrating the hydrated placeholder did not return it to online-only state.")
                    + details)
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> RunRemoteUpdateAfterDehydrateAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath)
        {
            byte[] updatedContent = Encoding.UTF8.GetBytes(
                "Cotton Sync Windows virtual files updated smoke content\n");
            string updatedText = Encoding.UTF8.GetString(updatedContent);
            string updatedHash = Convert.ToHexStringLower(SHA256.HashData(updatedContent));
            int downloadsBeforeUpdate = contentProvider.DownloadCount;
            context.CloudFiles.CreateFilePlaceholder(CreatePlaceholderRequest(
                context.SyncPair,
                RelativePlaceholderPath,
                updatedContent.LongLength,
                updatedHash));
            contentProvider.SetContent(updatedContent);
            int failures = await VerifyUpdatedPlaceholderMetadataAsync(
                    context.Output,
                    contentProvider,
                    placeholderPath,
                    updatedContent.LongLength,
                    downloadsBeforeUpdate)
                .ConfigureAwait(false);
            failures += await VerifyUpdatedPlaceholderHydrationAsync(
                    context,
                    contentProvider,
                    placeholderPath,
                    updatedText,
                    updatedHash,
                    downloadsBeforeUpdate)
                .ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> VerifyUpdatedPlaceholderMetadataAsync(
            TextWriter output,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath,
            long expectedSize,
            int downloadsBeforeUpdate)
        {
            FileInfo updatedInfo = new(placeholderPath);
            FileAttributes attributes = updatedInfo.Attributes;
            bool passed = updatedInfo.Length == expectedSize
                && HasRecallOnDataAccess(attributes)
                && contentProvider.DownloadCount == downloadsBeforeUpdate;
            if (passed)
            {
                await output.WriteLineAsync(
                        FormatCheck(true, "Remote update after dehydration refreshed placeholder metadata without downloading content.")
                        + " sizeBytes="
                        + updatedInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", attributes=" + FormatAttributes(attributes)
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await output.WriteLineAsync(
                    FormatCheck(false, "Remote update after dehydration did not refresh placeholder metadata correctly.")
                    + " expectedSizeBytes=" + expectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", actualSizeBytes="
                    + updatedInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", attributes=" + FormatAttributes(attributes)
                    + ", downloadsBeforeUpdate="
                    + downloadsBeforeUpdate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterUpdate="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> VerifyUpdatedPlaceholderHydrationAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath,
            string expectedText,
            string expectedHash,
            int downloadsBeforeUpdate)
        {
            string hydratedText = await context.ReadAllTextAsync(placeholderPath, context.CancellationToken)
                .ConfigureAwait(false);
            string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
            bool passed = string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                && contentProvider.DownloadCount == downloadsBeforeUpdate + 1;
            if (passed)
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(true, "Opening the updated dehydrated placeholder hydrated the latest remote content.")
                        + " sha256=" + hydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await context.Output.WriteLineAsync(
                    FormatCheck(false, "Opening the updated dehydrated placeholder did not hydrate the latest remote content.")
                    + " expectedSha256=" + expectedHash
                    + ", actualSha256=" + hydratedHash
                    + ", downloadsBeforeUpdate="
                    + downloadsBeforeUpdate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterHydration="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<DefaultVirtualFilesHydrationResult> ReconnectDefaultPlaceholderAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            IWindowsCloudFilesCallbackHandler callbackHandler,
            string placeholderPath,
            string expectedText,
            string expectedHash)
        {
            int downloadsBeforeReconnect = contentProvider.DownloadCount;
            await context.Output.WriteLineAsync(
                    "Disconnected Cloud Files sync root before reconnect smoke.")
                .ConfigureAwait(false);
            WindowsCloudFilesConnection connection = context.CloudFiles.ConnectSyncRoot(
                context.SyncPair,
                callbackHandler);
            await context.Output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root reconnected after provider restart simulation.")
                    + " root=" + connection.LocalRootPath)
                .ConfigureAwait(false);
            int failures = await VerifyReconnectedPlaceholderAsync(
                    context,
                    contentProvider,
                    placeholderPath,
                    expectedText,
                    expectedHash,
                    downloadsBeforeReconnect)
                .ConfigureAwait(false);
            return new DefaultVirtualFilesHydrationResult(failures, connection);
        }

        private static async Task<int> VerifyReconnectedPlaceholderAsync(
            WindowsVirtualFilesSmokeContext context,
            StaticSmokeContentProvider contentProvider,
            string placeholderPath,
            string expectedText,
            string expectedHash,
            int downloadsBeforeReconnect)
        {
            string hydratedText = await context.ReadAllTextAsync(placeholderPath, context.CancellationToken)
                .ConfigureAwait(false);
            string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hydratedText)));
            bool passed = string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                && contentProvider.DownloadCount == downloadsBeforeReconnect + 1;
            if (passed)
            {
                await context.Output.WriteLineAsync(
                        FormatCheck(true, "Reconnected Cloud Files callbacks hydrated the placeholder without duplicate registration.")
                        + " sha256=" + hydratedHash
                        + ", downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                return 0;
            }

            await context.Output.WriteLineAsync(
                    FormatCheck(false, "Reconnected Cloud Files callbacks did not hydrate the placeholder correctly.")
                    + " expectedSha256=" + expectedHash
                    + ", actualSha256=" + hydratedHash
                    + ", downloadsBeforeReconnect="
                    + downloadsBeforeReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterReconnect="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 1;
        }
    }
}
