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
