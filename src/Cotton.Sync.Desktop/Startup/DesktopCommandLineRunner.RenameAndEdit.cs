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
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
            DateTime deadline = DateTime.UtcNow + PropagationTimeout;
            int stableObservations = 0;
            string details = string.Empty;
            do
            {
                PresenceSnapshot presence = await CapturePresenceAsync(
                    options.LocalRoot!, options.SecondLocalRoot!, newPath, content, cancellationToken).ConfigureAwait(false);
                SyncStateEntry? first = await firstStore.GetAsync(firstPairId, newPath, cancellationToken)
                    .ConfigureAwait(false);
                SyncStateEntry? second = await secondStore.GetAsync(secondPairId, newPath, cancellationToken)
                    .ConfigureAwait(false);
                bool oldStateRemoved = await firstStore.GetAsync(firstPairId, oldPath, cancellationToken).ConfigureAwait(false) is null
                    && await secondStore.GetAsync(secondPairId, oldPath, cancellationToken).ConfigureAwait(false) is null;
                DesktopShellSnapshot firstShell = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
                DesktopShellSnapshot secondShell = await session.SecondController.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool idle = AreLiveSmokePairsIdle(
                    firstShell.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair.Id),
                    secondShell.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair.Id));
                details = presence.Details + ", oldStateRemoved=" + oldStateRemoved + ", idle=" + idle
                    + ", originalId=" + fileId + ", firstId=" + first?.RemoteFileId + ", secondId=" + second?.RemoteFileId
                    + ", expectedHash=" + expectedHash + ", firstHash=" + first?.RemoteContentHash
                    + ", secondHash=" + second?.RemoteContentHash
                    + ", firstLocalHash=" + first?.LocalContentHash + ", secondLocalHash=" + second?.LocalContentHash
                    + ", firstLocalSize=" + first?.LocalSizeBytes + ", secondLocalSize=" + second?.LocalSizeBytes
                    + ", firstHydration=" + first?.PlaceholderHydrationState
                    + ", secondHydration=" + second?.PlaceholderHydrationState
                    + ", firstStatus=" + firstShell.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair.Id)?.Status
                    + ", secondStatus=" + secondShell.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair.Id)?.Status
                    + ", firstError=" + firstShell.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair.Id)?.LastError
                    + ", secondError=" + secondShell.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair.Id)?.LastError;
                bool correct = presence.Passed && oldStateRemoved && idle
                    && first is not null && second is not null
                    && first.RemoteFileId == fileId && second.RemoteFileId == fileId
                    && first.RemoteContentHash == expectedHash && second.RemoteContentHash == expectedHash
                    && !File.Exists(FullPath(options.LocalRoot!, oldPath))
                    && !File.Exists(FullPath(options.SecondLocalRoot!, oldPath))
                    && !File.Exists(FullPath(options.LocalRoot!, temporaryPath))
                    && !File.Exists(FullPath(options.SecondLocalRoot!, temporaryPath));
                stableObservations = correct ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    await output.WriteLineAsync(FormatCheck(true,
                        "Rename and immediate edit preserved remote ID and exact content automatically: " + newPath))
                        .ConfigureAwait(false);
                    return 0;
                }
                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);
            details += ", firstPlaceholder=" + ReadLivePlaceholderVersion(FullPath(options.LocalRoot!, newPath))
                + ", secondPlaceholder=" + ReadLivePlaceholderVersion(FullPath(options.SecondLocalRoot!, newPath));
            await output.WriteLineAsync(FormatCheck(false,
                "Rename and immediate edit did not preserve identity, bytes and idle state: " + newPath) + " " + details)
                .ConfigureAwait(false);
            return 1;
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
