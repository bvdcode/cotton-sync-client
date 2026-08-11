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
    {        private const string DefaultSmokeRoot = @"S:\CottonSyncVfsQa\root";
        private const string DefaultSmokeParentRoot = @"S:\CottonSyncVfsQa";
        private const string RelativePlaceholderPath = "remote-only-smoke.txt";
        private const string LargeTreeDirectoryName = "large-tree";
        private const string NonEmptyPreservationDirectoryName = "pre-existing";
        private const string NonEmptyPreservationRootFilePath = NonEmptyPreservationDirectoryName + "/root-local.txt";
        private const string NonEmptyPreservationNestedFilePath = NonEmptyPreservationDirectoryName + "/nested/local-nested.txt";
        private const string NonEmptyPreservationRemoteOnlyDirectoryName = "remote-only";
        private const string NonEmptyPreservationRemoteOnlyFilePath = NonEmptyPreservationRemoteOnlyDirectoryName + "/cloud-only.txt";
        private const string ReplaceCloudOnlyDirectoryName = "replace-cloud-only";
        private const string ReplaceCloudOnlyRelativePath = ReplaceCloudOnlyDirectoryName + "/replace-smoke.txt";
        private const string ProviderWriteRenameSourcePath = "provider-write-rename/source.txt";
        private const string ProviderWriteRenameTargetPath = "provider-write-rename/renamed.txt";
        private const string ProviderMetadataUserEditPath = "provider-metadata-user-edit/edited.txt";
        private const string ProviderWriteMoveSourcePath = "provider-write-move/source/move.txt";
        private const string ProviderWriteMoveTargetPath = "provider-write-move/target/moved.txt";
        private const string ProviderWriteDirectoryMoveSourcePath = "provider-write-directory-move/source/folder";
        private const string ProviderWriteDirectoryMoveTargetPath = "provider-write-directory-move/target/folder";
        private const string ProviderWriteDirectoryMoveSourceFilePath = ProviderWriteDirectoryMoveSourcePath + "/nested/file.txt";
        private const string ProviderWriteDirectoryMoveTargetFilePath = ProviderWriteDirectoryMoveTargetPath + "/nested/file.txt";
        private const string ExcelAtomicSaveDirectoryPath = "excel-atomic-save";
        private const string ExcelAtomicSaveFirstWorkbookPath = ExcelAtomicSaveDirectoryPath + "/Budget.xlsx";
        private const string ExcelAtomicSaveSecondWorkbookPath = ExcelAtomicSaveDirectoryPath + "/Budget (1).xlsx";
        private const string ShellShareLinkDirectoryName = "share-link";
        private const string ShellShareLinkSyncedFilePath = ShellShareLinkDirectoryName + "/synced-file.txt";
        private const string ShellShareLinkRemoteOnlyFilePath = ShellShareLinkDirectoryName + "/remote-only-placeholder.txt";
        private const string ShellShareLinkHydratedFilePath = ShellShareLinkDirectoryName + "/hydrated-placeholder.txt";
        private const string ShellShareLinkFolderPath = ShellShareLinkDirectoryName + "/Folder";
        private const string ShellShareLinkLocalOnlyFilePath = ShellShareLinkDirectoryName + "/local-only.txt";
        private const string DesktopRootDirectoryName = "Desktop";
        private const string DesktopSessionRestoreDirectoryName = "DesktopSessionRestore";
        private const string DesktopRootRemoteFilePath = "desktop-cloud-file.txt";
        private const string AlwaysKeepPopulationDirectoryName = "always-keep-population";
        private const string AlwaysKeepPopulationEarlyDirectoryPath = AlwaysKeepPopulationDirectoryName + "/early";
        private const string AlwaysKeepPopulationEarlyFilePath = AlwaysKeepPopulationEarlyDirectoryPath + "/early.txt";
        private const string AlwaysKeepPopulationLateDirectoryPath = AlwaysKeepPopulationDirectoryName + "/late";
        private const string AlwaysKeepPopulationLateNestedDirectoryPath = AlwaysKeepPopulationLateDirectoryPath + "/nested";
        private const string AlwaysKeepPopulationLateFilePath = AlwaysKeepPopulationLateNestedDirectoryPath + "/late.txt";
        private const int DefaultLargeTreePlaceholderCount = 10_000;
        private const int LargeCleanupStateWriteBatchSize = 500;
        private const string LargeHydrationRelativePath = "large-hydration-smoke.bin";
        private const int LargeHydrationSizeBytes = 32 * 1024 * 1024;
        private const int LargeHydrationChunkBytes = 1024 * 1024;
        private const string SmokeContentText = "Cotton Sync Windows virtual files smoke content\n";
        private static readonly TimeSpan ExternalFileReadTimeout = TimeSpan.FromSeconds(30);
        private static readonly IReadOnlyDictionary<WindowsVirtualFilesSmokePhase, Func<WindowsVirtualFilesSmokeContext, Task<int>>>
            PhaseHandlers = new Dictionary<WindowsVirtualFilesSmokePhase, Func<WindowsVirtualFilesSmokeContext, Task<int>>>
            {
                [WindowsVirtualFilesSmokePhase.Default] = RunDefaultWindowsVirtualFilesSmokeAsync,
                [WindowsVirtualFilesSmokePhase.LeaveRegistered] = RunDefaultWindowsVirtualFilesSmokeAsync,
                [WindowsVirtualFilesSmokePhase.ReconnectExisting] = RunDefaultWindowsVirtualFilesSmokeAsync,
                [WindowsVirtualFilesSmokePhase.RemoteUpdateAfterDehydrate] = RunDefaultWindowsVirtualFilesSmokeAsync,
                [WindowsVirtualFilesSmokePhase.ExcelAtomicSave] = context => RunExcelAtomicSaveAsync(
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.ProviderMetadataUserEdit] = context => RunProviderMetadataUserEditAsync(
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.LocalRenameAfterProviderWrite] = context => RunLocalRenameAfterProviderWriteAsync(
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.LocalMoveAfterProviderWrite] = context => RunLocalMoveAfterProviderWriteAsync(
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.InitialStreamingLogging] = context => RunInitialStreamingLoggingAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    GetLargeTreePlaceholderCount(context.StartupOptions),
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.SteadyStateRepeat] = context => RunSteadyStateRepeatAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    GetLargeTreePlaceholderCount(context.StartupOptions),
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.LargeTree] = context => RunLargeTreeAsync(
                    context.StartupOptions,
                    context.Output,
                    context.CloudFiles,
                    context.SyncPair,
                    GetLargeTreePlaceholderCount(context.StartupOptions),
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.NonEmptyPreservation] = RunNonEmptyPreservationAsync,
                [WindowsVirtualFilesSmokePhase.RemovePairCleanup] = context => RunRemovePairCleanupAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.LargeRemovePairCleanup] = context => RunLargeRemovePairCleanupAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    GetLargeTreePlaceholderCount(context.StartupOptions),
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.TrayQuitDisconnect] = context => RunTrayQuitDisconnectAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.ExplorerFreeUpSpace] = RunExplorerFreeUpSpaceAsync,
                [WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeep] = RunExplorerAlwaysKeepAsync,
                [WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder] = RunExplorerAlwaysKeepAsync,
                [WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepDuringPopulation] =
                    RunExplorerAlwaysKeepDuringPopulationAsync,
                [WindowsVirtualFilesSmokePhase.ReplaceCloudOnlyUpload] = RunReplaceCloudOnlyUploadAsync,
                [WindowsVirtualFilesSmokePhase.ShellShareLinkTargets] = RunShellShareLinkTargetsAsync,
                [WindowsVirtualFilesSmokePhase.DesktopRootLifecycle] = context => RunDesktopRootLifecycleAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.DesktopSessionRestore] = context => RunDesktopSessionRestoreAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    context.Diagnostics,
                    context.CancellationToken),
                [WindowsVirtualFilesSmokePhase.LargeHydrationProgress] = context => RunLargeHydrationAsync(
                    context.Paths,
                    context.Output,
                    context.CloudFiles,
                    context.NativeApi,
                    context.SyncPair,
                    context.Diagnostics,
                    context.CancellationToken),
            };

        internal static async Task<int> PrepareStartupEnvironmentAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            string rootPath = ResolveSmokeRoot(startupOptions.LocalRoot);
            string? rootError = ValidateSmokeRoot(rootPath);
            if (rootError is not null)
            {
                await output.WriteLineAsync(FormatCheck(false, rootError)).ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string? setupError = await PrepareSmokeRootEnvironmentAsync(rootPath, output, cancellationToken)
                .ConfigureAwait(false);
            if (setupError is not null)
            {
                await output.WriteLineAsync(FormatCheck(false, setupError)).ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            return 0;
        }

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
            WindowsVirtualFilesSmokeContext? context = await CreateSmokeContextAsync(
                    paths,
                    startupOptions,
                    output,
                    cloudFilesAdapter,
                    readAllTextAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (context is null)
            {
                return 2;
            }

            return await PhaseHandlers[context.Phase](context).ConfigureAwait(false);
        }

        private static async Task<WindowsVirtualFilesSmokeContext?> CreateSmokeContextAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter? cloudFilesAdapter,
            Func<string, CancellationToken, Task<string>>? readAllTextAsync,
            CancellationToken cancellationToken)
        {
            string? rootPath = await PrepareSmokeExecutionRootAsync(startupOptions, output, cancellationToken)
                .ConfigureAwait(false);
            if (rootPath is null)
            {
                return null;
            }

            WindowsCloudFilesDiagnostics diagnostics = new();
            if (!WindowsVirtualFilesSmokePhaseCatalog.TryParse(
                    startupOptions.WindowsVirtualFilesSmokePhase,
                    out WindowsVirtualFilesSmokePhase phase))
            {
                string unsupportedPhase = (startupOptions.WindowsVirtualFilesSmokePhase ?? string.Empty).Trim();
                await WriteSmokeSetupFailureAsync(
                        output,
                        "Unsupported Windows virtual-files smoke phase: " + unsupportedPhase)
                    .ConfigureAwait(false);
                return null;
            }

            WindowsVirtualFilesSmokeCloudRuntime? cloudRuntime = await CreateSmokeCloudRuntimeAsync(
                    cloudFilesAdapter,
                    phase,
                    diagnostics,
                    output)
                .ConfigureAwait(false);
            if (cloudRuntime is null)
            {
                return null;
            }

            SyncPairSettings syncPair = CreateSyncPair(rootPath);
            Func<string, CancellationToken, Task<string>> reader =
                readAllTextAsync ?? ReadAllTextThroughExternalProcessAsync;
            return new WindowsVirtualFilesSmokeContext(
                paths,
                startupOptions,
                output,
                cloudRuntime.CloudFiles,
                cloudRuntime.NativeApi,
                syncPair,
                diagnostics,
                reader,
                phase,
                cancellationToken);
        }

        private static async Task<string?> PrepareSmokeExecutionRootAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await WriteSmokeSetupFailureAsync(
                        output,
                        "Windows Cloud Files API is only available on Windows.")
                    .ConfigureAwait(false);
                return null;
            }

            string rootPath = ResolveSmokeRoot(startupOptions.LocalRoot);
            await output.WriteLineAsync("Destructive root: " + rootPath).ConfigureAwait(false);
            string? rootError = ValidateSmokeRoot(rootPath);
            if (rootError is not null)
            {
                await WriteSmokeSetupFailureAsync(output, rootError).ConfigureAwait(false);
                return null;
            }

            string? setupError = await PrepareSmokeRootEnvironmentAsync(rootPath, output, cancellationToken)
                .ConfigureAwait(false);
            if (setupError is not null)
            {
                await WriteSmokeSetupFailureAsync(output, setupError).ConfigureAwait(false);
                return null;
            }

            return rootPath;
        }

        private static async Task<WindowsVirtualFilesSmokeCloudRuntime?> CreateSmokeCloudRuntimeAsync(
            IWindowsCloudFilesAdapter? configuredAdapter,
            WindowsVirtualFilesSmokePhase phase,
            WindowsCloudFilesDiagnostics diagnostics,
            TextWriter output)
        {
            if (configuredAdapter is not null)
            {
                return new WindowsVirtualFilesSmokeCloudRuntime(configuredAdapter, NativeApi: null);
            }

            IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar =
                WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            bool requiresExplorerAvailabilityVerbs =
                WindowsVirtualFilesSmokePhaseCatalog.RequiresExplorerAvailabilityVerbs(phase);
            if (requiresExplorerAvailabilityVerbs && storageProviderRegistrar is null)
            {
                await WriteSmokeSetupFailureAsync(
                        output,
                        "Explorer availability smoke requires the packaged Windows shell helper beside the desktop app.")
                    .ConfigureAwait(false);
                return null;
            }

            IWindowsCloudFilesNativeApi nativeApi = new WindowsCloudFilesNativeApi();
            IWindowsCloudFilesAdapter cloudFiles = new WindowsCloudFilesAdapter(
                nativeApi: nativeApi,
                storageProviderRegistrar: storageProviderRegistrar,
                diagnostics: diagnostics);
            return new WindowsVirtualFilesSmokeCloudRuntime(cloudFiles, nativeApi);
        }

        private static async Task WriteSmokeSetupFailureAsync(TextWriter output, string message)
        {
            await output.WriteLineAsync(FormatCheck(false, message)).ConfigureAwait(false);
            await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
        }
}
}
