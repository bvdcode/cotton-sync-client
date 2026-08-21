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
        private static bool AreLatePopulationDirectoriesPinned(
            AlwaysKeepPopulationWorkContext populationContext,
            string lateDirectoryPath,
            string lateNestedDirectoryPath)
        {
            return populationContext.LateDescendantsInheritedAvailability
                && HasPinned(File.GetAttributes(lateDirectoryPath))
                && HasPinned(File.GetAttributes(lateNestedDirectoryPath));
        }

        private static bool ArePopulationFilesReady(
            bool filesHydrated,
            string earlyText,
            string lateText,
            int downloadCount,
            int downloadsBeforeRead,
            int expectedDownloadCount)
        {
            return filesHydrated
                && string.Equals(earlyText, SmokeContentText, StringComparison.Ordinal)
                && string.Equals(lateText, SmokeContentText, StringComparison.Ordinal)
                && downloadCount == downloadsBeforeRead
                && downloadCount == expectedDownloadCount;
        }

        private static async Task<int> VerifyAlwaysKeepPopulationUnpinAsync(
            WindowsVirtualFilesSmokeContext context,
            SyncPairRunner runner,
            StaticSmokeContentProvider contentProvider,
            AlwaysKeepPopulationPaths paths)
        {
            int downloadsBeforeUnpin = contentProvider.DownloadCount;
            WindowsShellVerbInvocationResult verb = await InvokeExplorerAlwaysKeepAsync(
                    paths.FolderPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            bool[] attributeResults = await WaitForPopulationAttributesAsync(
                    paths.DirectoryPaths,
                    paths.FilePaths,
                    IsHydratedWithoutPin,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await RunAlwaysKeepPopulationAvailabilityAsync(runner, context.CancellationToken).ConfigureAwait(false);
            string earlyText = await ReadAllTextThroughExternalProcessAsync(paths.FilePaths[0], context.CancellationToken)
                .ConfigureAwait(false);
            string lateText = await ReadAllTextThroughExternalProcessAsync(paths.FilePaths[1], context.CancellationToken)
                .ConfigureAwait(false);
            bool passed = DidPopulationUnpinPass(
                verb,
                attributeResults,
                contentProvider.DownloadCount,
                downloadsBeforeUnpin,
                paths,
                earlyText,
                lateText);
            return await WriteCheckAsync(
                    context.Output,
                    passed,
                    "Second Explorer Always keep invocation removed pin without deleting hydrated content.",
                    "downloadsBeforeUnpin="
                    + downloadsBeforeUnpin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterUnpin="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", parentAttributes=" + FormatAttributes(File.GetAttributes(paths.FolderPath))
                    + ", earlyFileAttributes=" + FormatAttributes(File.GetAttributes(paths.FilePaths[0]))
                    + ", lateFileAttributes=" + FormatAttributes(File.GetAttributes(paths.FilePaths[1])))
                .ConfigureAwait(false);
        }

        private static async Task<int> VerifyAlwaysKeepPopulationRepinAsync(
            WindowsVirtualFilesSmokeContext context,
            SyncPairRunner runner,
            StaticSmokeContentProvider contentProvider,
            AlwaysKeepPopulationPaths paths)
        {
            int downloadsBeforeRepin = contentProvider.DownloadCount;
            WindowsShellVerbInvocationResult verb = await InvokeExplorerAlwaysKeepAsync(
                    paths.FolderPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            bool[] attributeResults = await WaitForPopulationAttributesAsync(
                    paths.DirectoryPaths,
                    paths.FilePaths,
                    HasPinned,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await RunAlwaysKeepPopulationAvailabilityAsync(runner, context.CancellationToken).ConfigureAwait(false);
            bool passed = DidPopulationRepinPass(
                verb,
                attributeResults,
                contentProvider.DownloadCount,
                downloadsBeforeRepin,
                paths);
            return await WriteCheckAsync(
                    context.Output,
                    passed,
                    "Third Explorer Always keep invocation restored pin without redownloading.",
                    "downloadsBeforeRepin="
                    + downloadsBeforeRepin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", downloadsAfterRepin="
                    + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        private static async Task<bool[]> WaitForPopulationAttributesAsync(
            IEnumerable<string> directoryPaths,
            IEnumerable<string> filePaths,
            Func<FileAttributes, bool> predicate,
            CancellationToken cancellationToken)
        {
            return await Task.WhenAll(
                    directoryPaths
                        .Concat(filePaths)
                        .Select(path => WaitForAttributesAsync(
                            path,
                            predicate,
                            TimeSpan.FromSeconds(15),
                            cancellationToken)))
                .ConfigureAwait(false);
        }

        private static Task RunAlwaysKeepPopulationAvailabilityAsync(
            SyncPairRunner runner,
            CancellationToken cancellationToken)
        {
            return runner.SyncNowAsync(
                SyncRunRequest.ForLocalChangedPaths([AlwaysKeepPopulationDirectoryName]),
                cancellationToken);
        }

        private static bool DidPopulationUnpinPass(
            WindowsShellVerbInvocationResult verb,
            IReadOnlyList<bool> attributeResults,
            int downloadCount,
            int downloadsBeforeUnpin,
            AlwaysKeepPopulationPaths paths,
            string earlyText,
            string lateText)
        {
            return verb.Invoked
                && attributeResults.All(static result => result)
                && downloadCount == downloadsBeforeUnpin
                && paths.DirectoryPaths.All(path => IsHydratedWithoutPin(File.GetAttributes(path)))
                && paths.FilePaths.All(path => IsHydratedWithoutPin(File.GetAttributes(path)))
                && string.Equals(earlyText, SmokeContentText, StringComparison.Ordinal)
                && string.Equals(lateText, SmokeContentText, StringComparison.Ordinal);
        }

        private static bool DidPopulationRepinPass(
            WindowsShellVerbInvocationResult verb,
            IReadOnlyList<bool> attributeResults,
            int downloadCount,
            int downloadsBeforeRepin,
            AlwaysKeepPopulationPaths paths)
        {
            return verb.Invoked
                && attributeResults.All(static result => result)
                && downloadCount == downloadsBeforeRepin
                && paths.DirectoryPaths.All(path => HasPinned(File.GetAttributes(path)))
                && paths.FilePaths.All(path => IsHydratedPinnedPlaceholder(File.GetAttributes(path)));
        }

        private static async Task HoldAlwaysKeepPopulationRootAsync(
            WindowsVirtualFilesSmokeContext context,
            string folderPath)
        {
            TimeSpan holdDuration = context.StartupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder;
            if (holdDuration <= TimeSpan.Zero)
            {
                return;
            }

            await context.Output.WriteLineAsync(
                    "Holding pinned population root for "
                    + holdDuration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds; inspect " + folderPath + " in Explorer before cleanup starts.")
                .ConfigureAwait(false);
            await Task.Delay(holdDuration, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
