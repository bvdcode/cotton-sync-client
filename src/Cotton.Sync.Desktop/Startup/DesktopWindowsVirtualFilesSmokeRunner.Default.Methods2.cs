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
