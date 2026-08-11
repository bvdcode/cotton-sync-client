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
        private static async Task<int> RunExplorerAlwaysKeepDuringPopulationAsync(
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
                await output.WriteLineAsync(
                    FormatCheck(false, "Always keep during population smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string folderPath = ToFullPath(rootPath, AlwaysKeepPopulationDirectoryName);
            string earlyDirectoryPath = ToFullPath(rootPath, AlwaysKeepPopulationEarlyDirectoryPath);
            string earlyFilePath = ToFullPath(rootPath, AlwaysKeepPopulationEarlyFilePath);
            string lateDirectoryPath = ToFullPath(rootPath, AlwaysKeepPopulationLateDirectoryPath);
            string lateNestedDirectoryPath = ToFullPath(rootPath, AlwaysKeepPopulationLateNestedDirectoryPath);
            string lateFilePath = ToFullPath(rootPath, AlwaysKeepPopulationLateFilePath);
            string[] directoryPaths = [folderPath, earlyDirectoryPath, lateDirectoryPath, lateNestedDirectoryPath];
            string[] filePaths = [earlyFilePath, lateFilePath];
            AlwaysKeepPopulationPaths populationPaths = new(folderPath, directoryPaths, filePaths);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            StaticSmokeContentProvider contentProvider = new(expectedContent);
            WindowsCloudFilesHydrationCoordinator callbackHandler = new(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                diagnostics);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            LocalChangeSuppression localChangeSuppression = new LocalChangeSuppression();
            DesktopCloudFilesPlaceholderWriter placeholderWriter = new(
                cloudFilesAdapter: cloudFiles,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."),
                localChangeSuppression: localChangeSuppression);
            WindowsVirtualFilesDehydrationPairWork availabilityWork = new(
                new FailOnInnerSyncPairWork("Always keep during population smoke must not run inner sync for availability-only changes."),
                stateStore,
                cloudFiles,
                new LocalFileScanner(),
                diagnostics,
                localChangeSuppression: localChangeSuppression);
            TaskCompletionSource<bool> earlyPopulationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continuePopulation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AlwaysKeepPopulationWorkContext populationWorkContext = new()
            {
                SyncPair = syncPair,
                AvailabilityWork = availabilityWork,
                PopulationObserver = placeholderWriter,
                CreateDirectoryAsync = (relativePath, token) => CreateAlwaysKeepPopulationDirectoryAsync(
                    placeholderWriter,
                    stateStore,
                    syncPair,
                    relativePath,
                    token),
                CreateFileAsync = (relativePath, token) => CreateAlwaysKeepPopulationFileAsync(
                    placeholderWriter,
                    stateStore,
                    syncPair,
                    expectedContent,
                    expectedHash,
                    relativePath,
                    token),
                EarlyPopulationReady = earlyPopulationReady,
                ContinuePopulation = continuePopulation,
                EvaluateLateDescendantAvailability = () => HaveLateDescendantsInheritedAvailability(
                    lateDirectoryPath,
                    lateNestedDirectoryPath,
                    lateFilePath),
            };
            DelegateSyncPairWork pairWork = new((_, request, token) =>
                RunAlwaysKeepPopulationWorkAsync(populationWorkContext, request, token));
            SyncPairRunner runner = new SyncPairRunner(syncPair, pairWork);
            FileSystemLocalSyncRootWatcher watcher = new FileSystemLocalSyncRootWatcher(syncPair.Id, rootPath);
            AlwaysKeepPopulationWatcherCoordinator watcherCoordinator = new(
                localChangeSuppression,
                runner,
                folderPath,
                AlwaysKeepPopulationDirectoryName,
                continuePopulation.Task);
            WindowsCloudFilesConnection? connection = null;
            Task? initialRun = null;
            int failures = 0;
            watcher.Changed += watcherCoordinator.OnWatcherChanged;
            try
            {
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    FormatCheck(true, "Packaged Cloud Files root and watcher prepared for Always keep during population.")
                    + " root="
                    + rootPath)
                    .ConfigureAwait(false);

                initialRun = runner.SyncNowAsync(
                    SyncRunRequest.ForFull(SyncRunCause.InitialPopulation),
                    cancellationToken);
                bool initialReady = await WaitForTaskAsync(
                        earlyPopulationReady.Task,
                        TimeSpan.FromSeconds(20),
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        initialReady,
                        "Initial population paused after creating the early subtree.",
                        string.Empty)
                    .ConfigureAwait(false);

                ShellVerbInvocationResult verbResult = await InvokeExplorerAlwaysKeepAsync(folderPath, cancellationToken)
                    .ConfigureAwait(false);
                failures += await WriteCheckAsync(
                        output,
                        verbResult.Invoked,
                        "Explorer shell invoked Always keep on the parent folder during population.",
                        "verb=" + (verbResult.InvokedVerbName ?? "missing")
                        + ", availableVerbs=" + string.Join("|", verbResult.AvailableVerbNames))
                    .ConfigureAwait(false);

                bool parentPinned = await WaitForAttributesAsync(
                        folderPath,
                        HasPinned,
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                bool watcherQueued = await WaitForTaskAsync(
                        watcherCoordinator.RequestQueued,
                        TimeSpan.FromSeconds(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                bool watcherPassed = parentPinned && watcherQueued && watcherCoordinator.QueuedDuringPopulation;
                failures += await WriteCheckAsync(
                        output,
                        watcherPassed,
                        "Explorer Always keep watcher event queued while initial population was active.",
                        "parentAttributes=" + FormatAttributes(File.GetAttributes(folderPath))
                        + ", runnerState=" + runner.Status.State)
                    .ConfigureAwait(false);

                continuePopulation.TrySetResult(true);
                await initialRun.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);

                bool lateDirectoriesPinned = AreLatePopulationDirectoriesPinned(
                    populationWorkContext,
                    lateDirectoryPath,
                    lateNestedDirectoryPath);
                failures += await WriteCheckAsync(
                        output,
                        lateDirectoriesPinned,
                        "Late-created descendants inherited Always keep before initial population completed.",
                        "lateDirectoryAttributes=" + FormatAttributes(File.GetAttributes(lateDirectoryPath))
                        + ", lateNestedAttributes=" + FormatAttributes(File.GetAttributes(lateNestedDirectoryPath)))
                    .ConfigureAwait(false);

                bool filesHydrated = filePaths.All(path => IsHydratedPinnedPlaceholder(File.GetAttributes(path)));
                int downloadsBeforeRead = contentProvider.DownloadCount;
                string earlyText = await ReadAllTextThroughExternalProcessAsync(earlyFilePath, cancellationToken)
                    .ConfigureAwait(false);
                string lateText = await ReadAllTextThroughExternalProcessAsync(lateFilePath, cancellationToken)
                    .ConfigureAwait(false);
                bool allFilesPassed = ArePopulationFilesReady(
                    filesHydrated,
                    earlyText,
                    lateText,
                    contentProvider.DownloadCount,
                    downloadsBeforeRead,
                    filePaths.Length);
                failures += await WriteCheckAsync(
                        output,
                        allFilesPassed,
                        "All early and late files became pinned and hydrated.",
                        "downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                bool allDirectoriesPinned = directoryPaths.All(path => HasPinned(File.GetAttributes(path)));
                failures += await WriteCheckAsync(
                        output,
                        allDirectoriesPinned,
                        "All Always-keep directories were pinned after population.",
                        "At least one Always-keep directory remained unpinned after population.")
                    .ConfigureAwait(false);

                failures += await VerifyAlwaysKeepPopulationUnpinAsync(
                        context,
                        runner,
                        contentProvider,
                        populationPaths)
                    .ConfigureAwait(false);
                failures += await VerifyAlwaysKeepPopulationRepinAsync(
                        context,
                        runner,
                        contentProvider,
                        populationPaths)
                    .ConfigureAwait(false);
                await HoldAlwaysKeepPopulationRootAsync(context, folderPath).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                continuePopulation.TrySetResult(true);
                if (initialRun is not null)
                {
                    try
                    {
                        await initialRun.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                watcher.Changed -= watcherCoordinator.OnWatcherChanged;
                await watcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await watcher.DisposeAsync().ConfigureAwait(false);
                connection?.Dispose();
                failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
            }

            return await WriteSmokeResultAsync(output, diagnostics, failures).ConfigureAwait(false);
        }

        private static async Task CreateAlwaysKeepPopulationDirectoryAsync(
            DesktopCloudFilesPlaceholderWriter placeholderWriter,
            ISyncStateStore stateStore,
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            await placeholderWriter
                .BeforeCreateDirectoryAsync(CreateDirectoryRequest(syncPair, relativePath), cancellationToken)
                .ConfigureAwait(false);
            await stateStore
                .UpsertAsync(CreateDirectoryState(syncPair, relativePath), cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task CreateAlwaysKeepPopulationFileAsync(
            IRemoteFilePlaceholderWriter placeholderWriter,
            ISyncStateStore stateStore,
            SyncPairSettings syncPair,
            byte[] expectedContent,
            string expectedHash,
            string relativePath,
            CancellationToken cancellationToken)
        {
            RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(
                syncPair,
                relativePath,
                expectedContent.LongLength,
                expectedHash);
            RemoteFilePlaceholderResult placeholder = await placeholderWriter
                .CreatePlaceholderAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await stateStore
                .UpsertAsync(CreatePlaceholderState(syncPair, request, placeholder), cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task RunAlwaysKeepPopulationWorkAsync(
            AlwaysKeepPopulationWorkContext context,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            if (!request.IsFull)
            {
                await context.AvailabilityWork
                    .RunOnceAsync(context.SyncPair, request, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            using IDisposable population = context.PopulationObserver.BeginPopulation(
                context.SyncPair.Id.ToString("D"),
                context.SyncPair.LocalRootPath);
            await context.CreateDirectoryAsync(AlwaysKeepPopulationDirectoryName, cancellationToken)
                .ConfigureAwait(false);
            await context.CreateDirectoryAsync(AlwaysKeepPopulationEarlyDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            await context.CreateFileAsync(AlwaysKeepPopulationEarlyFilePath, cancellationToken).ConfigureAwait(false);
            context.EarlyPopulationReady.TrySetResult(true);
            await context.ContinuePopulation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await context.CreateDirectoryAsync(AlwaysKeepPopulationLateDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            await context.CreateDirectoryAsync(AlwaysKeepPopulationLateNestedDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            await context.CreateFileAsync(AlwaysKeepPopulationLateFilePath, cancellationToken).ConfigureAwait(false);
            context.LateDescendantsInheritedAvailability = context.EvaluateLateDescendantAvailability();
        }

        private static bool HaveLateDescendantsInheritedAvailability(
            string lateDirectoryPath,
            string lateNestedDirectoryPath,
            string lateFilePath)
        {
            return HasPinned(File.GetAttributes(lateDirectoryPath))
                && HasPinned(File.GetAttributes(lateNestedDirectoryPath))
                && IsHydratedPinnedPlaceholder(File.GetAttributes(lateFilePath));
        }

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
            ShellVerbInvocationResult verb = await InvokeExplorerAlwaysKeepAsync(
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
            ShellVerbInvocationResult verb = await InvokeExplorerAlwaysKeepAsync(
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
            ShellVerbInvocationResult verb,
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
            ShellVerbInvocationResult verb,
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
