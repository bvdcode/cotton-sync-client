// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<int> RunLiveRestoreBeforeDeleteApplicationAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            CottonCloudClient client,
            IReadOnlyList<LiveSyncSmokeRestoreFile> files,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            bool retained = true;
            SqliteSyncStateStore firstStore = new(session.FirstPaths.SyncStateDatabasePath);
            SqliteSyncStateStore secondStore = new(session.SecondPaths.SyncStateDatabasePath);
            session.FirstManifestBarrier.ArmChangeFeed();
            session.SecondManifestBarrier.ArmChangeFeed();
            try
            {
                foreach (LiveSyncSmokeRestoreFile file in files)
                {
                    await client.Files.DeleteAsync(file.RemoteFileId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                await Task.WhenAll(
                    session.FirstManifestBarrier.WaitUntilBlockedAsync(RestoreManifestHoldTimeout, cancellationToken),
                    session.SecondManifestBarrier.WaitUntilBlockedAsync(RestoreManifestHoldTimeout, cancellationToken))
                    .ConfigureAwait(false);
                foreach (LiveSyncSmokeRestoreFile file in files)
                {
                    SyncStateEntry? first = await firstStore.GetAsync(
                        session.FirstPair!.Id.ToString("D"), file.RelativePath, cancellationToken).ConfigureAwait(false);
                    SyncStateEntry? second = await secondStore.GetAsync(
                        session.SecondPair!.Id.ToString("D"), file.RelativePath, cancellationToken).ConfigureAwait(false);
                    retained &= first?.RemoteFileId == file.RemoteFileId && second?.RemoteFileId == file.RemoteFileId
                        && File.Exists(FullPath(options.LocalRoot!, file.RelativePath))
                        && File.Exists(FullPath(options.SecondLocalRoot!, file.RelativePath));
                    await client.Files.RestoreAsync(file.RemoteFileId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                session.FirstManifestBarrier.Release();
                session.SecondManifestBarrier.Release();
            }
            bool restored = await WaitForLiveRestoreFilesAsync(
                options, session, files, restored: true, cancellationToken).ConfigureAwait(false);
            bool passed = retained && restored;
            await output.WriteLineAsync(FormatCheck(passed,
                "Restores before delete-event application preserved all eleven IDs and bytes without another deletion."))
                .ConfigureAwait(false);
            return passed ? 0 : 1;
        }
    }
}
