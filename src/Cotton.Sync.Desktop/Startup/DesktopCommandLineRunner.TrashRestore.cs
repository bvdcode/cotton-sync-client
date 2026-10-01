// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private const int RestoreBurstFileCount = 11;
        private const int RestoreBurstFirstBatchCount = 4;
        private static readonly TimeSpan RestoreManifestHoldTimeout = TimeSpan.FromSeconds(15);

        private static async Task<int> RunLiveTrashRestoreBurstAsync(
            DesktopStartupOptions options,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (seededFiles.Count < RestoreBurstFileCount)
            {
                await output.WriteLineAsync(FormatCheck(false,
                    "Trash restore acceptance requires at least eleven seeded files.")).ConfigureAwait(false);
                return 1;
            }
            SqliteSyncStateStore store = new(session.FirstPaths.SyncStateDatabasePath);
            List<LiveSyncSmokeRestoreFile> files = new(RestoreBurstFileCount);
            foreach (LiveSyncSmokeSeededLocalFile seed in seededFiles.Take(RestoreBurstFileCount))
            {
                SyncStateEntry? entry = await store.GetAsync(
                    session.FirstPair!.Id.ToString("D"), seed.RelativePath, cancellationToken).ConfigureAwait(false);
                if (entry?.RemoteFileId is not Guid id)
                {
                    await output.WriteLineAsync(FormatCheck(false,
                        "Trash restore source has no remote identity: " + seed.RelativePath)).ConfigureAwait(false);
                    return 1;
                }
                files.Add(new LiveSyncSmokeRestoreFile(seed.RelativePath, id, seed.Sha256));
            }
            using HttpClient httpClient = DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30));
            FileCottonTokenStore tokens = new(session.FirstPaths.TokenStorePath);
            CottonSdkOptions sdkOptions = new()
            {
                BaseAddress = options.ServerUrl!,
                UserAgent = DesktopDeviceIdentity.CreateUserAgent(),
                DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
            };
            await using CottonCloudClient client = new(httpClient, tokens, sdkOptions, new DesktopTraceLoggerFactory());
            foreach (LiveSyncSmokeRestoreFile file in files)
            {
                await client.Files.DeleteAsync(file.RemoteFileId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            if (!await WaitForLiveRestoreFilesAsync(options, session, files, restored: false, cancellationToken)
                .ConfigureAwait(false))
            {
                await output.WriteLineAsync(FormatCheck(false,
                    "Trashed files were not removed automatically from both clients.")).ConfigureAwait(false);
                return 1;
            }
            session.FirstManifestBarrier.Arm(session.FirstPair!.RemoteRootNodeId);
            session.SecondManifestBarrier.Arm(session.SecondPair!.RemoteRootNodeId);
            try
            {
                foreach (LiveSyncSmokeRestoreFile file in files.Take(RestoreBurstFirstBatchCount))
                {
                    await client.Files.RestoreAsync(file.RemoteFileId, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                await Task.WhenAll(
                    session.FirstManifestBarrier.WaitUntilBlockedAsync(RestoreManifestHoldTimeout, cancellationToken),
                    session.SecondManifestBarrier.WaitUntilBlockedAsync(RestoreManifestHoldTimeout, cancellationToken))
                    .ConfigureAwait(false);
                foreach (LiveSyncSmokeRestoreFile file in files.Skip(RestoreBurstFirstBatchCount))
                {
                    await client.Files.RestoreAsync(file.RemoteFileId, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                await output.WriteLineAsync(FormatCheck(true,
                    "Seven additional restores arrived while both clients held the first restore manifest."))
                    .ConfigureAwait(false);
            }
            finally
            {
                session.FirstManifestBarrier.Release();
                session.SecondManifestBarrier.Release();
            }
            bool passed = await WaitForLiveRestoreFilesAsync(options, session, files, restored: true, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(FormatCheck(passed,
                "Eleven trash restores across a held manifest preserved IDs and bytes automatically on both clients."))
                .ConfigureAwait(false);
            if (!passed)
            {
                return 1;
            }
            return await RunLiveRestoreBeforeDeleteApplicationAsync(
                options, session, client, files, output, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> WaitForLiveRestoreFilesAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            IReadOnlyList<LiveSyncSmokeRestoreFile> files,
            bool restored,
            CancellationToken cancellationToken)
        {
            SqliteSyncStateStore firstStore = new(session.FirstPaths.SyncStateDatabasePath);
            SqliteSyncStateStore secondStore = new(session.SecondPaths.SyncStateDatabasePath);
            DateTime deadline = DateTime.UtcNow + PropagationTimeout;
            int stableObservations = 0;
            do
            {
                bool correct = true;
                foreach (LiveSyncSmokeRestoreFile file in files)
                {
                    correct &= await IsLiveRestoreFileCorrectAsync(
                        options.LocalRoot!, session.FirstPair!.Id, firstStore, file, restored, cancellationToken)
                        .ConfigureAwait(false);
                    correct &= await IsLiveRestoreFileCorrectAsync(
                        options.SecondLocalRoot!, session.SecondPair!.Id, secondStore, file, restored, cancellationToken)
                        .ConfigureAwait(false);
                }
                DesktopShellSnapshot firstShell = await session.FirstController.LoadAsync(cancellationToken).ConfigureAwait(false);
                DesktopShellSnapshot secondShell = await session.SecondController.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool idle = AreLiveSmokePairsIdle(
                    firstShell.SyncPairs.FirstOrDefault(item => item.Id == session.FirstPair!.Id),
                    secondShell.SyncPairs.FirstOrDefault(item => item.Id == session.SecondPair!.Id));
                stableObservations = correct && idle ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    return true;
                }
                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private static async Task<bool> IsLiveRestoreFileCorrectAsync(
            string root,
            Guid pairId,
            SqliteSyncStateStore store,
            LiveSyncSmokeRestoreFile file,
            bool restored,
            CancellationToken cancellationToken)
        {
            SyncStateEntry? entry = await store.GetAsync(pairId.ToString("D"), file.RelativePath, cancellationToken)
                .ConfigureAwait(false);
            string path = FullPath(root, file.RelativePath);
            if (!restored)
            {
                return entry is null && !File.Exists(path);
            }
            if (entry?.RemoteFileId != file.RemoteFileId || entry.RemoteContentHash != file.Sha256)
            {
                return false;
            }
            TextReadSnapshot read = await TryReadAllTextForLiveSmokeAsync(path, cancellationToken).ConfigureAwait(false);
            return read.Exists && read.Read
                && await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false) == file.Sha256;
        }
    }
}
