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
        private static async Task<int> RunProviderMetadataUserEditAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string filePath = ToFullPath(rootPath, ProviderMetadataUserEditPath);
            const string userContent = "user content after provider metadata finalization";
            LocalChangeSuppression suppression = new();
            RecordingRenameSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator? coordinator = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, SmokeContentText, cancellationToken).ConfigureAwait(false);
                suppression.SuppressProviderMetadataWrite(syncPair.Id, rootPath, ProviderMetadataUserEditPath);
                coordinator = new LocalChangeSyncCoordinator(
                    new SingleSyncPairSettingsStore(syncPair),
                    supervisor,
                    new FileSystemLocalSyncRootWatcherFactory(),
                    debounceInterval: TimeSpan.FromMilliseconds(100),
                    changeSuppression: suppression,
                    maxDebounceDelay: TimeSpan.FromSeconds(1));
                await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.NotContentIndexed);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                failures += await VerifyProviderMetadataEchoSuppressionAsync(
                    output,
                    supervisor.SyncNowCallCount).ConfigureAwait(false);

                await File.WriteAllTextAsync(filePath, userContent, cancellationToken).ConfigureAwait(false);
                SyncRunRequest request = await supervisor
                    .WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

                failures += await VerifyProviderMetadataUserEditScopeAsync(output, request).ConfigureAwait(false);
                failures += await VerifyProviderMetadataUserEditRequestCountAsync(
                    output,
                    supervisor.SyncNowCallCount).ConfigureAwait(false);

                string actualContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                failures += await VerifyProviderMetadataUserContentAsync(
                    output,
                    actualContent,
                    userContent).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                if (coordinator is not null)
                {
                    await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                PrepareRoot(rootPath);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task<int> VerifyProviderMetadataEchoSuppressionAsync(
            TextWriter output,
            int syncRequestCount)
        {
            if (syncRequestCount == 0)
            {
                await output.WriteLineAsync(
                    FormatCheck(true, "Provider metadata attribute echo was suppressed without starting sync."))
                    .ConfigureAwait(false);
                return 0;
            }

            await output.WriteLineAsync(
                FormatCheck(false, "Provider metadata attribute echo started an unexpected sync request.")
                + " requests="
                + syncRequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> VerifyProviderMetadataUserEditScopeAsync(
            TextWriter output,
            SyncRunRequest request)
        {
            bool exactUserEditScope = !request.IsFull
                && request.LocalChangedPaths.Count == 1
                && request.LocalChangedPaths.Contains(ProviderMetadataUserEditPath, StringComparer.OrdinalIgnoreCase)
                && request.LocalDeletedPaths.Count == 0;
            if (exactUserEditScope)
            {
                await output.WriteLineAsync(
                    FormatCheck(true, "Real watcher preserved a user content edit after provider metadata finalization."))
                    .ConfigureAwait(false);
                return 0;
            }

            await output.WriteLineAsync(
                FormatCheck(false, "Provider metadata suppression hid or widened the user content edit scope.")
                + " requestedPaths="
                + string.Join(",", request.LocalChangedPaths))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> VerifyProviderMetadataUserEditRequestCountAsync(
            TextWriter output,
            int syncRequestCount)
        {
            if (syncRequestCount == 1)
            {
                await output.WriteLineAsync(
                    FormatCheck(true, "Post-finalization content edit stayed scoped and emitted one request."))
                    .ConfigureAwait(false);
                return 0;
            }

            await output.WriteLineAsync(
                FormatCheck(false, "Post-finalization content edit emitted an unexpected request count.")
                + " requests="
                + syncRequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> VerifyProviderMetadataUserContentAsync(
            TextWriter output,
            string actualContent,
            string expectedContent)
        {
            if (string.Equals(actualContent, expectedContent, StringComparison.Ordinal))
            {
                return 0;
            }

            await output.WriteLineAsync(
                FormatCheck(false, "Post-finalization user content was not preserved on disk."))
                .ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> RunLocalRenameAfterProviderWriteAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string sourcePath = ToFullPath(rootPath, ProviderWriteRenameSourcePath);
            string targetPath = ToFullPath(rootPath, ProviderWriteRenameTargetPath);
            LocalChangeSuppression suppression = new();
            RecordingRenameSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator? coordinator = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, SmokeContentText, cancellationToken).ConfigureAwait(false);
                suppression.SuppressProviderWrite(syncPair.Id, rootPath, ProviderWriteRenameSourcePath);
                coordinator = new LocalChangeSyncCoordinator(
                    new SingleSyncPairSettingsStore(syncPair),
                    supervisor,
                    new FileSystemLocalSyncRootWatcherFactory(),
                    debounceInterval: TimeSpan.FromMilliseconds(100),
                    changeSuppression: suppression,
                    maxDebounceDelay: TimeSpan.FromSeconds(1));
                await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                File.Move(sourcePath, targetPath);
                SyncRunRequest request = await supervisor
                    .WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

                bool preservedBothPaths = !request.IsFull
                    && request.LocalChangedPaths.Count == 2
                    && request.LocalChangedPaths.Contains(ProviderWriteRenameSourcePath, StringComparer.OrdinalIgnoreCase)
                    && request.LocalChangedPaths.Contains(ProviderWriteRenameTargetPath, StringComparer.OrdinalIgnoreCase)
                    && request.LocalDeletedPaths.Count == 0;
                failures += await WriteOutcomeAsync(
                        output,
                        preservedBothPaths,
                        "Real watcher preserved both paths for a user rename after provider write suppression.",
                        "Provider write suppression hid part of the user rename scope.")
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        supervisor.SyncNowCallCount == 1,
                        "Provider-suppressed user rename stayed scoped and emitted one request.",
                        "Provider-suppressed user rename emitted an unexpected request count.",
                        "requests="
                        + supervisor.SyncNowCallCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        !File.Exists(sourcePath) && File.Exists(targetPath),
                        "File-system rename completed without duplicating the local file.",
                        "File-system rename did not leave exactly the target file.")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                if (coordinator is not null)
                {
                    await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                PrepareRoot(rootPath);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task<int> RunExcelAtomicSaveAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string firstWorkbookPath = ToFullPath(rootPath, ExcelAtomicSaveFirstWorkbookPath);
            string secondWorkbookPath = ToFullPath(rootPath, ExcelAtomicSaveSecondWorkbookPath);
            RecordingRenameSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator? coordinator = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(firstWorkbookPath)!);
                await File.WriteAllTextAsync(firstWorkbookPath, "initial-budget", cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(secondWorkbookPath, "initial-budget-1", cancellationToken).ConfigureAwait(false);
                coordinator = new LocalChangeSyncCoordinator(
                    new SingleSyncPairSettingsStore(syncPair),
                    supervisor,
                    new FileSystemLocalSyncRootWatcherFactory(),
                    debounceInterval: TimeSpan.FromMilliseconds(500),
                    maxDebounceDelay: TimeSpan.FromSeconds(2));
                await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                ReplaceLikeExcel(firstWorkbookPath, "updated-budget");
                ReplaceLikeExcel(secondWorkbookPath, "updated-budget-1");
                SyncRunRequest request = await supervisor
                    .WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);

                bool exactWorkbookScope = !request.IsFull
                    && request.LocalChangedPaths.Count == 2
                    && request.LocalChangedPaths.Contains(ExcelAtomicSaveFirstWorkbookPath, StringComparer.OrdinalIgnoreCase)
                    && request.LocalChangedPaths.Contains(ExcelAtomicSaveSecondWorkbookPath, StringComparer.OrdinalIgnoreCase)
                    && request.LocalDeletedPaths.Count == 0;
                failures += await WriteOutcomeAsync(
                        output,
                        exactWorkbookScope,
                        "Excel-style atomic saves stayed scoped to exactly the two workbook paths.",
                        "Excel-style atomic saves included a parent, lock, or temporary path in the sync request.",
                        "requestedPaths=" + string.Join(",", request.LocalChangedPaths))
                    .ConfigureAwait(false);
                bool temporaryArtifactsGone = !Directory
                    .EnumerateFileSystemEntries(Path.GetDirectoryName(firstWorkbookPath)!)
                    .Any(path => Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)
                        || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
                failures += await WriteOutcomeAsync(
                        output,
                        temporaryArtifactsGone,
                        "Excel lock and temporary artifacts were ignored and removed.",
                        "Excel lock or temporary artifacts remained after the save burst.")
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        supervisor.SyncNowCallCount == 1,
                        "Two Excel-style saves emitted one debounced scoped request.",
                        "Excel-style saves emitted an unexpected request count.",
                        "requests="
                        + supervisor.SyncNowCallCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                if (coordinator is not null)
                {
                    await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                PrepareRoot(rootPath);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static void ReplaceLikeExcel(string targetPath, string content)
        {
            string directoryPath = Path.GetDirectoryName(targetPath)!;
            string lockPath = Path.Combine(directoryPath, "~$" + Path.GetFileName(targetPath));
            string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(lockPath, "excel-lock");
                File.WriteAllText(temporaryPath, content);
                File.Replace(temporaryPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            finally
            {
                File.Delete(temporaryPath);
                File.Delete(lockPath);
            }
        }

        private static async Task<int> RunLocalMoveAfterProviderWriteAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            string rootPath = syncPair.LocalRootPath;
            string sourcePath = ToFullPath(rootPath, ProviderWriteMoveSourcePath);
            string targetPath = ToFullPath(rootPath, ProviderWriteMoveTargetPath);
            string directorySourcePath = ToFullPath(rootPath, ProviderWriteDirectoryMoveSourcePath);
            string directoryTargetPath = ToFullPath(rootPath, ProviderWriteDirectoryMoveTargetPath);
            string directorySourceFilePath = ToFullPath(rootPath, ProviderWriteDirectoryMoveSourceFilePath);
            string directoryTargetFilePath = ToFullPath(rootPath, ProviderWriteDirectoryMoveTargetFilePath);
            LocalChangeSuppression suppression = new();
            RecordingRenameSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator? coordinator = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(sourcePath, SmokeContentText, cancellationToken).ConfigureAwait(false);
                suppression.SuppressProviderMetadataWrite(syncPair.Id, rootPath, ProviderWriteMoveSourcePath);
                coordinator = new LocalChangeSyncCoordinator(
                    new SingleSyncPairSettingsStore(syncPair),
                    supervisor,
                    new FileSystemLocalSyncRootWatcherFactory(),
                    debounceInterval: TimeSpan.FromMilliseconds(100),
                    changeSuppression: suppression,
                    maxDebounceDelay: TimeSpan.FromSeconds(1));
                await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                File.Move(sourcePath, targetPath);
                SyncRunRequest request = await supervisor
                    .WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

                failures += await WriteOutcomeAsync(
                        output,
                        IsExpectedFileMoveRequest(request),
                        "Real watcher preserved delete and create paths for a cross-directory move after provider metadata finalization.",
                        "Provider metadata suppression hid part of the cross-directory move scope.")
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        supervisor.SyncNowCallCount == 1,
                        "Cross-directory move stayed scoped and emitted one request.",
                        "Cross-directory move emitted an unexpected request count.",
                        "requests="
                        + supervisor.SyncNowCallCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        IsFileMoveComplete(sourcePath, targetPath),
                        "File-system cross-directory move left exactly the target file.",
                        "File-system cross-directory move did not leave exactly the target file.")
                    .ConfigureAwait(false);

                await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                coordinator = null;
                PrepareRoot(rootPath);
                Directory.CreateDirectory(Path.GetDirectoryName(directorySourceFilePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(directoryTargetPath)!);
                await File.WriteAllTextAsync(directorySourceFilePath, SmokeContentText, cancellationToken).ConfigureAwait(false);
                LocalChangeSuppression directorySuppression = new();
                RecordingRenameSyncSupervisor directorySupervisor = new();
                directorySuppression.SuppressProviderMetadataWrite(
                    syncPair.Id,
                    rootPath,
                    ProviderWriteDirectoryMoveSourcePath);
                coordinator = new LocalChangeSyncCoordinator(
                    new SingleSyncPairSettingsStore(syncPair),
                    directorySupervisor,
                    new FileSystemLocalSyncRootWatcherFactory(),
                    debounceInterval: TimeSpan.FromMilliseconds(100),
                    changeSuppression: directorySuppression,
                    maxDebounceDelay: TimeSpan.FromSeconds(1));
                await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                Directory.Move(directorySourcePath, directoryTargetPath);
                SyncRunRequest directoryRequest = await directorySupervisor
                    .WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

                failures += await WriteOutcomeAsync(
                        output,
                        IsExpectedDirectoryMoveRequest(directoryRequest),
                        "Real watcher preserved the deleted source and created target for a directory move after placeholder repair metadata finalization.",
                        "Placeholder repair metadata suppression hid part of the directory move scope.")
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        directorySupervisor.SyncNowCallCount == 1,
                        "Directory move after placeholder repair stayed scoped and emitted one request.",
                        "Directory move after placeholder repair emitted an unexpected request count.",
                        "requests="
                        + directorySupervisor.SyncNowCallCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                failures += await WriteOutcomeAsync(
                        output,
                        IsDirectoryMoveComplete(
                            directorySourcePath,
                            directoryTargetPath,
                            directorySourceFilePath,
                            directoryTargetFilePath),
                        "File-system directory move preserved the nested file only at the target.",
                        "File-system directory move did not preserve exactly the target subtree.")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures = await RecordSmokeFailureAsync(output, failures, exception).ConfigureAwait(false);
            }
            finally
            {
                if (coordinator is not null)
                {
                    await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                PrepareRoot(rootPath);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static bool IsExpectedFileMoveRequest(SyncRunRequest request)
        {
            return !request.IsFull
                && request.LocalChangedPaths.Contains(
                    ProviderWriteMoveSourcePath,
                    StringComparer.OrdinalIgnoreCase)
                && request.LocalChangedPaths.Contains(
                    ProviderWriteMoveTargetPath,
                    StringComparer.OrdinalIgnoreCase)
                && request.LocalDeletedPaths.Count == 1
                && request.LocalDeletedPaths.Contains(
                    ProviderWriteMoveSourcePath,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFileMoveComplete(string sourcePath, string targetPath)
        {
            return !File.Exists(sourcePath) && File.Exists(targetPath);
        }

        private static bool IsExpectedDirectoryMoveRequest(SyncRunRequest request)
        {
            return !request.IsFull
                && request.LocalChangedPaths.Contains(
                    ProviderWriteDirectoryMoveSourcePath,
                    StringComparer.OrdinalIgnoreCase)
                && request.LocalChangedPaths.Contains(
                    ProviderWriteDirectoryMoveTargetPath,
                    StringComparer.OrdinalIgnoreCase)
                && request.LocalDeletedPaths.Contains(
                    ProviderWriteDirectoryMoveSourcePath,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsDirectoryMoveComplete(
            string sourcePath,
            string targetPath,
            string sourceFilePath,
            string targetFilePath)
        {
            return !Directory.Exists(sourcePath)
                && Directory.Exists(targetPath)
                && !File.Exists(sourceFilePath)
                && File.Exists(targetFilePath);
        }
}
}
