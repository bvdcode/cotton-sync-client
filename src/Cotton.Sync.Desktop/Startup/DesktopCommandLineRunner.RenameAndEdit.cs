// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<int> RunLiveRenameAndEditAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            bool fromSecondClient,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string root = options.LocalRoot!;
            string oldPath = LocalUploadPath;
            string newPath = LocalRenamedPath;
            if (fromSecondClient)
            {
                root = options.SecondLocalRoot!;
                oldPath = RemoteOriginPath;
                newPath = RemoteRenamedPath;
            }
            string temporaryPath = oldPath + ".rename-stage";
            string content = "Immediate content change after rename: " + newPath;
            SqliteSyncStateStore firstStore = new(session.FirstPaths.SyncStateDatabasePath);
            SqliteSyncStateStore secondStore = new(session.SecondPaths.SyncStateDatabasePath);
            string firstPairId = session.FirstPair!.Id.ToString("D");
            string secondPairId = session.SecondPair!.Id.ToString("D");
            SyncStateEntry? beforeFirst = await firstStore.GetAsync(firstPairId, oldPath, cancellationToken)
                .ConfigureAwait(false);
            SyncStateEntry? beforeSecond = await secondStore.GetAsync(secondPairId, oldPath, cancellationToken)
                .ConfigureAwait(false);
            if (beforeFirst?.RemoteFileId is not Guid fileId || beforeSecond?.RemoteFileId != fileId)
            {
                await output.WriteLineAsync(FormatCheck(false,
                    "Rename and edit require one shared remote file identity before mutation.")).ConfigureAwait(false);
                return 1;
            }
            int failure = await EnsureRenameSourceReadableAsync(
                root, oldPath, "Rename and edit source is readable.", output, cancellationToken).ConfigureAwait(false);
            if (failure != 0)
            {
                return failure;
            }
            string originalContent = await ReadAllTextThroughExternalProcessAsync(
                FullPath(root, oldPath), cancellationToken).ConfigureAwait(false);
            LiveSyncManifestBarrier sourceBarrier = session.FirstManifestBarrier;
            string receiverRoot = options.SecondLocalRoot!;
            Guid receiverPairId = session.SecondPair!.Id;
            SqliteSyncStateStore receiverStore = secondStore;
            if (fromSecondClient)
            {
                sourceBarrier = session.SecondManifestBarrier;
                receiverRoot = options.LocalRoot!;
                receiverPairId = session.FirstPair.Id;
                receiverStore = firstStore;
            }
            session.FirstManifestBarrier.ObserveFile(fileId);
            session.SecondManifestBarrier.ObserveFile(fileId);
            sourceBarrier.ArmRename(fileId);
            string expectedHash;
            try
            {
                if (fromSecondClient)
                {
                    File.Move(FullPath(root, oldPath), FullPath(root, temporaryPath));
                    File.Move(FullPath(root, temporaryPath), FullPath(root, newPath));
                }
                else
                {
                    File.Move(FullPath(root, oldPath), FullPath(root, newPath));
                }
                await WriteFileAsync(root, newPath, content, cancellationToken).ConfigureAwait(false);
                byte[] expectedBytes = await File.ReadAllBytesAsync(FullPath(root, newPath), cancellationToken)
                    .ConfigureAwait(false);
                expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
                await sourceBarrier.WaitUntilBlockedAsync(RestoreManifestHoldTimeout, cancellationToken)
                    .ConfigureAwait(false);
                bool previousVersionRead = await WaitForLiveRenamedPreviousVersionAsync(
                    receiverRoot, receiverPairId, receiverStore, newPath, fileId,
                    originalContent, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(FormatCheck(previousVersionRead,
                    "Receiver read the renamed previous version while the content update was held: " + newPath))
                    .ConfigureAwait(false);
                if (!previousVersionRead)
                {
                    return 1;
                }
            }
            finally
            {
                sourceBarrier.Release();
            }
            return await WaitForRenameAndEditConvergenceAsync(options, session, firstStore, secondStore,
                (oldPath, newPath, temporaryPath), content, expectedHash, fileId, output, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> WaitForRenameAndEditConvergenceAsync(
            DesktopStartupOptions options, DesktopLiveSyncSmokeSession session,
            SqliteSyncStateStore firstStore, SqliteSyncStateStore secondStore,
            (string OldPath, string NewPath, string TemporaryPath) paths,
            string content, string expectedHash, Guid fileId, TextWriter output, CancellationToken cancellationToken)
        {
            string firstPairId = session.FirstPair!.Id.ToString("D");
            string secondPairId = session.SecondPair!.Id.ToString("D");
            DateTime deadline = DateTime.UtcNow + PropagationTimeout;
            int stableObservations = 0;
            string details = string.Empty;
            do
            {
                PresenceSnapshot presence = await CapturePresenceAsync(
                    options.LocalRoot!, options.SecondLocalRoot!, paths.NewPath, content, cancellationToken).ConfigureAwait(false);
                SyncStateEntry? first = await firstStore.GetAsync(firstPairId, paths.NewPath, cancellationToken)
                    .ConfigureAwait(false);
                SyncStateEntry? second = await secondStore.GetAsync(secondPairId, paths.NewPath, cancellationToken)
                    .ConfigureAwait(false);
                bool oldStateRemoved = await firstStore.GetAsync(firstPairId, paths.OldPath, cancellationToken).ConfigureAwait(false) is null
                    && await secondStore.GetAsync(secondPairId, paths.OldPath, cancellationToken).ConfigureAwait(false) is null;
                DesktopShellSnapshot firstShell = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
                DesktopShellSnapshot secondShell = await session.SecondController.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool idle = AreLiveSmokePairsIdle(
                    firstShell.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair.Id),
                    secondShell.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair.Id));
                details = presence.Details + ", oldStateRemoved=" + oldStateRemoved + ", idle=" + idle
                    + ", originalId=" + fileId + ", expectedHash=" + expectedHash
                    + FormatRenameEntry("first", first) + FormatRenameEntry("second", second)
                    + FormatRenamePair("first", firstShell, session.FirstPair!.Id)
                    + FormatRenamePair("second", secondShell, session.SecondPair!.Id);
                bool correct = presence.Passed && oldStateRemoved && idle
                    && HasExpectedRenameIdentity(first, second, fileId, expectedHash)
                    && AreRenameSourcePathsRemoved(options, paths.OldPath, paths.TemporaryPath);
                stableObservations = correct ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    await output.WriteLineAsync(FormatCheck(true,
                        "Rename and immediate edit preserved remote ID and exact content automatically: " + paths.NewPath))
                        .ConfigureAwait(false);
                    return 0;
                }
                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);
            details += ", firstPlaceholder=" + ReadLivePlaceholderVersion(FullPath(options.LocalRoot!, paths.NewPath))
                + ", secondPlaceholder=" + ReadLivePlaceholderVersion(FullPath(options.SecondLocalRoot!, paths.NewPath));
            SyncChangeCursor firstCursor = await firstStore.GetChangeCursorAsync(firstPairId, cancellationToken)
                .ConfigureAwait(false);
            SyncChangeCursor secondCursor = await secondStore.GetChangeCursorAsync(secondPairId, cancellationToken)
                .ConfigureAwait(false);
            details += ", firstCursor=" + firstCursor.LastCursor + ", secondCursor=" + secondCursor.LastCursor
                + ", firstResponses=" + session.FirstManifestBarrier.ReadFileResponses(fileId)
                + ", secondResponses=" + session.SecondManifestBarrier.ReadFileResponses(fileId);
            await output.WriteLineAsync(FormatCheck(false,
                "Rename and immediate edit did not preserve identity, bytes and idle state: " + paths.NewPath) + " " + details)
                .ConfigureAwait(false);
            return 1;
        }

        private static string FormatRenameEntry(string name, SyncStateEntry? entry)
        {
            return ", " + name + "Id=" + entry?.RemoteFileId + ", " + name + "Hash=" + entry?.RemoteContentHash
                + ", " + name + "LocalHash=" + entry?.LocalContentHash + ", " + name + "LocalSize=" + entry?.LocalSizeBytes
                + ", " + name + "Hydration=" + entry?.PlaceholderHydrationState;
        }

        private static string FormatRenamePair(string name, DesktopShellSnapshot shell, Guid pairId)
        {
            DesktopSyncPairSnapshot? pair = shell.SyncPairs.FirstOrDefault(item => item.Id == pairId);
            return ", " + name + "Status=" + pair?.Status + ", " + name + "Error=" + pair?.LastError;
        }

        private static bool HasExpectedRenameIdentity(SyncStateEntry? first, SyncStateEntry? second,
            Guid fileId, string expectedHash)
        {
            return first is not null && second is not null
                && first.RemoteFileId == fileId && second.RemoteFileId == fileId
                && first.RemoteContentHash == expectedHash && second.RemoteContentHash == expectedHash;
        }

        private static bool AreRenameSourcePathsRemoved(DesktopStartupOptions options, string oldPath, string temporaryPath)
        {
            return !File.Exists(FullPath(options.LocalRoot!, oldPath))
                && !File.Exists(FullPath(options.SecondLocalRoot!, oldPath))
                && !File.Exists(FullPath(options.LocalRoot!, temporaryPath))
                && !File.Exists(FullPath(options.SecondLocalRoot!, temporaryPath));
        }

        private static string ReadLivePlaceholderVersion(string path)
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(path))
            {
                return "unavailable";
            }
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return "regular-file:" + attributes;
            }
            try
            {
                WindowsCloudFilesNativeApi nativeApi = new();
                WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(
                    nativeApi.GetPlaceholderIdentity(path));
                return identity.RelativePath + ":" + identity.ContentHash + ":" + identity.SizeBytes
                    + ":" + attributes;
            }
            catch (WindowsCloudFilesNativeException exception)
            {
                return exception.Operation + ":" + exception.HResult.ToString("X8",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
