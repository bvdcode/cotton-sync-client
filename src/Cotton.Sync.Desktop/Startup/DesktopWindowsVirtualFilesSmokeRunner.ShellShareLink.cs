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
        private static async Task<int> RunShellShareLinkTargetsAsync(WindowsVirtualFilesSmokeContext context)
        {
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
                        FormatCheck(false, "Shell share-link VFS target smoke requires the native Windows Cloud Files API."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = syncPair.LocalRootPath;
            string shareLinkDirectoryPath = Path.Combine(rootPath, ShellShareLinkDirectoryName);
            string syncedFilePath = ToFullPath(rootPath, ShellShareLinkSyncedFilePath);
            string remoteOnlyFilePath = ToFullPath(rootPath, ShellShareLinkRemoteOnlyFilePath);
            string hydratedFilePath = ToFullPath(rootPath, ShellShareLinkHydratedFilePath);
            string folderPath = ToFullPath(rootPath, ShellShareLinkFolderPath);
            string localOnlyFilePath = ToFullPath(rootPath, ShellShareLinkLocalOnlyFilePath);
            byte[] syncedContent = Encoding.UTF8.GetBytes("Cotton Sync VFS share-link synced file\n");
            byte[] remoteOnlyContent = Encoding.UTF8.GetBytes("Cotton Sync VFS share-link remote-only placeholder\n");
            byte[] hydratedContent = Encoding.UTF8.GetBytes("Cotton Sync VFS share-link hydrated placeholder\n");
            byte[] localOnlyContent = Encoding.UTF8.GetBytes("Cotton Sync VFS share-link local-only file\n");
            string remoteOnlyHash = Convert.ToHexStringLower(SHA256.HashData(remoteOnlyContent));
            string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(hydratedContent));
            Dictionary<string, byte[]> contentByPath = new(StringComparer.OrdinalIgnoreCase)
            {
                [SyncPath.Normalize(ShellShareLinkRemoteOnlyFilePath)] = remoteOnlyContent,
                [SyncPath.Normalize(ShellShareLinkHydratedFilePath)] = hydratedContent,
            };
            DictionarySmokeContentProvider contentProvider = new(contentByPath);
            WindowsCloudFilesHydrationCoordinator callbackHandler = new(
                contentProvider,
                nativeApi,
                Path.Combine(paths.DataDirectory, "vfs-shell-share-link-temp"),
                diagnostics);
            SqliteSyncPairSettingsStore pairStore = new(paths.AppDatabasePath);
            SqliteSyncStateStore stateStore = new(paths.SyncStateDatabasePath);
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                PrepareRoot(rootPath);
                Directory.CreateDirectory(shareLinkDirectoryPath);
                await File.WriteAllBytesAsync(syncedFilePath, syncedContent, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(localOnlyFilePath, localOnlyContent, cancellationToken).ConfigureAwait(false);
                await pairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await pairStore.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);
                await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await stateStore.DeletePairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                        FormatCheck(true, "Isolated QA root prepared for VFS shell share-link target smoke.")
                        + " root="
                        + rootPath)
                    .ConfigureAwait(false);

                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                        FormatCheck(true, "Cloud Files sync root connected for VFS shell share-link target smoke.")
                        + " root="
                        + connection.LocalRootPath)
                    .ConfigureAwait(false);

                cloudFiles.CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, ShellShareLinkFolderPath));
                await stateStore
                    .UpsertAsync(CreateDirectoryState(syncPair, ShellShareLinkFolderPath), cancellationToken)
                    .ConfigureAwait(false);

                RemoteFilePlaceholderRequest remoteOnlyRequest = CreatePlaceholderRequest(
                    syncPair,
                    ShellShareLinkRemoteOnlyFilePath,
                    remoteOnlyContent.LongLength,
                    remoteOnlyHash);
                ApplyShellShareLinkRemoteIdentity(remoteOnlyRequest.RemoteFile, 1);
                RemoteFilePlaceholderResult remoteOnlyPlaceholder = cloudFiles.CreateFilePlaceholder(remoteOnlyRequest);
                await stateStore
                    .UpsertAsync(CreatePlaceholderState(syncPair, remoteOnlyRequest, remoteOnlyPlaceholder), cancellationToken)
                    .ConfigureAwait(false);

                RemoteFilePlaceholderRequest hydratedRequest = CreatePlaceholderRequest(
                    syncPair,
                    ShellShareLinkHydratedFilePath,
                    hydratedContent.LongLength,
                    hydratedHash);
                ApplyShellShareLinkRemoteIdentity(hydratedRequest.RemoteFile, 2);
                RemoteFilePlaceholderResult hydratedPlaceholder = cloudFiles.CreateFilePlaceholder(hydratedRequest);
                SyncStateEntry hydratedState = CreatePlaceholderState(syncPair, hydratedRequest, hydratedPlaceholder);
                await stateStore.UpsertAsync(hydratedState, cancellationToken).ConfigureAwait(false);

                await stateStore
                    .UpsertAsync(
                        CreateShellShareLinkSyncedFileState(
                            syncPair,
                            ShellShareLinkSyncedFilePath,
                            syncedContent,
                            3),
                        cancellationToken)
                    .ConfigureAwait(false);

                await output.WriteLineAsync(
                        FormatCheck(true, "VFS shell share-link smoke seeded synced, placeholder, folder, and local-only targets.")
                        + " targets=5")
                    .ConfigureAwait(false);

                string hydratedText = await ReadAllTextThroughExternalProcessAsync(hydratedFilePath, cancellationToken)
                    .ConfigureAwait(false);
                bool hydrated = string.Equals(
                        hydratedText,
                        Encoding.UTF8.GetString(hydratedContent),
                        StringComparison.Ordinal)
                    && contentProvider.DownloadedPaths.Contains(
                        SyncPath.Normalize(ShellShareLinkHydratedFilePath),
                        StringComparer.OrdinalIgnoreCase);
                if (hydrated)
                {
                    hydratedState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                    await stateStore.UpsertAsync(hydratedState, cancellationToken).ConfigureAwait(false);
                }

                failures += await WritePassFailAsync(
                        output,
                        hydrated,
                        "VFS shell share-link hydrated placeholder fetched exact remote content before copy.",
                        " downloads="
                        + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);

                VfsShellShareLinkScenario[] scenarios =
                [
                    new(
                        "VFS synced file share link copied",
                        syncedFilePath,
                        ShellShareLinkSyncedFilePath,
                        ShellShareLinkTargetKind.File,
                        ExpectCopied: true,
                        ExpectedFailureReason: null),
                    new(
                        "VFS remote-only placeholder share link copied",
                        remoteOnlyFilePath,
                        ShellShareLinkRemoteOnlyFilePath,
                        ShellShareLinkTargetKind.File,
                        ExpectCopied: true,
                        ExpectedFailureReason: null),
                    new(
                        "VFS hydrated placeholder share link copied",
                        hydratedFilePath,
                        ShellShareLinkHydratedFilePath,
                        ShellShareLinkTargetKind.File,
                        ExpectCopied: true,
                        ExpectedFailureReason: null),
                    new(
                        "VFS folder share link copied",
                        folderPath,
                        ShellShareLinkFolderPath,
                        ShellShareLinkTargetKind.Directory,
                        ExpectCopied: true,
                        ExpectedFailureReason: null),
                    new(
                        "VFS local-only item is rejected without clipboard write",
                        localOnlyFilePath,
                        ShellShareLinkLocalOnlyFilePath,
                        ShellShareLinkTargetKind.Unknown,
                        ExpectCopied: false,
                        ExpectedFailureReason: "target-missing-baseline"),
                ];
                foreach (VfsShellShareLinkScenario scenario in scenarios)
                {
                    failures += await RunVfsShellShareLinkCopyCaseAsync(context, scenario).ConfigureAwait(false);
                }

                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        ShellShareLinkRemoteOnlyFilePath,
                        "VFS shell share-link remote-only placeholder Cloud Files status was finalized.",
                        allowPartialDirectory: true)
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        ShellShareLinkHydratedFilePath,
                        "VFS shell share-link hydrated placeholder Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyCloudFilesInSyncStateAsync(
                        output,
                        cloudFiles,
                        syncPair,
                        ShellShareLinkFolderPath,
                        "VFS shell share-link folder Cloud Files status was finalized.")
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        remoteOnlyFilePath,
                        "VFS shell share-link remote-only placeholder",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        hydratedFilePath,
                        "VFS shell share-link hydrated placeholder",
                        cancellationToken)
                    .ConfigureAwait(false);
                failures += await VerifyExplorerShellSettledStatusAsync(
                        output,
                        folderPath,
                        "VFS shell share-link folder",
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

        private static SyncStateEntry CreateDirectoryState(SyncPairSettings syncPair, string relativePath)
        {
            RemoteDirectoryMaterializationRequest request = CreateDirectoryRequest(syncPair, relativePath);
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = request.RemoteDirectory.Id,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncStateEntry CreateShellShareLinkSyncedFileState(
            SyncPairSettings syncPair,
            string relativePath,
            byte[] content,
            int identityIndex)
        {
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = contentHash,
                LocalSizeBytes = content.LongLength,
                RemoteSizeBytes = content.LongLength,
                RemoteNodeId = Guid.CreateVersion7(),
                RemoteFileId = Guid.CreateVersion7(),
                RemoteFileManifestId = Guid.CreateVersion7(),
                RemoteOriginalNodeFileId = Guid.CreateVersion7(),
                RemoteContentHash = contentHash,
                RemoteETag = "vfs-shell-share-link-etag-" + identityIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PlaceholderHydrationState = SyncPlaceholderHydrationState.None,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static async Task<int> RunVfsShellShareLinkCopyCaseAsync(
            WindowsVirtualFilesSmokeContext context,
            VfsShellShareLinkScenario scenario)
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--data-dir",
                    context.Paths.DataDirectory,
                    "--copy-shell-share-link",
                    scenario.SelectedPath,
                ]);
            using StringWriter caseOutput = new StringWriter();
            VfsShellShareLinkSmokeClient shareLinkClient = new();
            VfsShellShareLinkSmokeClipboardService clipboard = new();
            VfsShellShareLinkSmokeNotificationService notifications = new();

            int exitCode = await DesktopCommandLineRunner.RunShellShareLinkCopyAsync(
                    context.Paths,
                    options,
                    caseOutput,
                    shareLinkClient: shareLinkClient,
                    clipboardService: clipboard,
                    notificationService: notifications,
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);

            string report = caseOutput.ToString();
            bool passed = DoesShellShareLinkCopyMatch(scenario, exitCode, clipboard)
                && DoesShellShareLinkFailureMatch(scenario, report)
                && DoesShellShareLinkNotificationMatch(scenario, notifications)
                && DoesShellShareLinkStatusMatch(scenario, report)
                && DoesShellShareLinkTargetMatch(scenario, shareLinkClient.LastTarget)
                && DoesShellShareLinkReportHideLocalPath(scenario, report)
                && DoesShellShareLinkResultMatch(scenario, report);

            return await WriteCheckAsync(
                    context.Output,
                    passed,
                    scenario.Label,
                    "exitCode="
                    + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", copied="
                    + (clipboard.CopiedText is not null).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", notification="
                    + (!string.IsNullOrWhiteSpace(notifications.LastMessage))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        private static bool DoesShellShareLinkCopyMatch(
            VfsShellShareLinkScenario scenario,
            int exitCode,
            VfsShellShareLinkSmokeClipboardService clipboard)
        {
            return scenario.ExpectCopied
                ? exitCode == 0 && !string.IsNullOrWhiteSpace(clipboard.CopiedText)
                : exitCode != 0 && clipboard.CopiedText is null;
        }

        private static bool DoesShellShareLinkFailureMatch(VfsShellShareLinkScenario scenario, string report)
        {
            return scenario.ExpectedFailureReason is null
                ? !report.Contains("FailureReason:", StringComparison.Ordinal)
                : report.Contains("FailureReason: " + scenario.ExpectedFailureReason, StringComparison.Ordinal);
        }

        private static bool DoesShellShareLinkNotificationMatch(
            VfsShellShareLinkScenario scenario,
            VfsShellShareLinkSmokeNotificationService notifications)
        {
            return scenario.ExpectCopied
                ? string.Equals(notifications.LastMessage, "Share link copied to clipboard.", StringComparison.Ordinal)
                : !string.IsNullOrWhiteSpace(notifications.LastMessage);
        }

        private static bool DoesShellShareLinkStatusMatch(VfsShellShareLinkScenario scenario, string report)
        {
            string expectedStatus = scenario.ExpectCopied ? "Status: resolved" : "Status: missing-baseline";
            return report.Contains(expectedStatus, StringComparison.Ordinal);
        }

        private static bool DoesShellShareLinkTargetMatch(
            VfsShellShareLinkScenario scenario,
            ShellShareLinkTarget? target)
        {
            if (!scenario.ExpectCopied)
            {
                return target is null;
            }

            return target is not null
                && string.Equals(
                    target.RelativePath,
                    SyncPath.Normalize(scenario.ExpectedRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                && target.Kind == scenario.ExpectedKind
                && HasExpectedShellShareLinkIdentity(scenario.ExpectedKind, target);
        }

        private static bool HasExpectedShellShareLinkIdentity(
            ShellShareLinkTargetKind expectedKind,
            ShellShareLinkTarget target)
        {
            return expectedKind switch
            {
                ShellShareLinkTargetKind.File => target.RemoteFileId.HasValue,
                ShellShareLinkTargetKind.Directory => target.RemoteNodeId.HasValue,
                ShellShareLinkTargetKind.Unknown => false,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedKind), expectedKind, null),
            };
        }

        private static bool DoesShellShareLinkReportHideLocalPath(
            VfsShellShareLinkScenario scenario,
            string report)
        {
            return !report.Contains(scenario.SelectedPath, StringComparison.OrdinalIgnoreCase)
                && !report.Contains(Path.GetFileName(scenario.SelectedPath), StringComparison.OrdinalIgnoreCase);
        }

        private static bool DoesShellShareLinkResultMatch(VfsShellShareLinkScenario scenario, string report)
        {
            string expectedResult = scenario.ExpectCopied ? "Result: passed" : "Result: failed";
            return report.Contains(expectedResult, StringComparison.Ordinal);
        }
}
}
