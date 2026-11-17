// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task WriteLiveRestoreFailureAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            IReadOnlyList<LiveSyncSmokeRestoreFile> files,
            bool restored,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            SqliteSyncStateStore firstStore = new(session.FirstPaths.SyncStateDatabasePath);
            SqliteSyncStateStore secondStore = new(session.SecondPaths.SyncStateDatabasePath);
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> hashes =
                await ReadLiveRestoreHashesAsync(options, files, restored, cancellationToken).ConfigureAwait(false);
            foreach (LiveSyncSmokeRestoreFile file in files)
            {
                bool firstCorrect = await IsLiveRestoreFileCorrectAsync(
                    options.LocalRoot!, session.FirstPair!.Id, firstStore, file, restored, hashes, cancellationToken)
                    .ConfigureAwait(false);
                bool secondCorrect = await IsLiveRestoreFileCorrectAsync(
                    options.SecondLocalRoot!, session.SecondPair!.Id, secondStore, file, restored, hashes, cancellationToken)
                    .ConfigureAwait(false);
                SyncStateEntry? first = await firstStore.GetAsync(
                    session.FirstPair.Id.ToString("D"), file.RelativePath, cancellationToken).ConfigureAwait(false);
                SyncStateEntry? second = await secondStore.GetAsync(
                    session.SecondPair.Id.ToString("D"), file.RelativePath, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"Restore file: path={file.RelativePath}, expectedId={file.RemoteFileId}, expectedHash={file.Sha256}, "
                    + $"firstCorrect={firstCorrect}, secondCorrect={secondCorrect}"
                    + FormatRenameEntry("first", first) + FormatRenameEntry("second", second))
                    .ConfigureAwait(false);
            }
            DesktopShellSnapshot firstShell = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondShell = await session.SecondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("Restore status:"
                + FormatRenamePair("first", firstShell, session.FirstPair!.Id)
                + FormatRenamePair("second", secondShell, session.SecondPair!.Id)).ConfigureAwait(false);
        }
    }
}
