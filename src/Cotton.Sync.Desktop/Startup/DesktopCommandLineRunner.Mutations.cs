// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<IReadOnlyList<LiveSyncSmokeSeededLocalFile>> SeedExistingLocalFilesAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (startupOptions.LiveSyncSmokeSeedFileCount.HasValue)
            {
                return await SeedExistingLocalBurstAsync(
                        startupOptions,
                        startupOptions.LiveSyncSmokeSeedFileCount.Value,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string firstContent = "Cotton Sync Desktop live smoke pre-existing file from client A"
                + Environment.NewLine
                + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + Environment.NewLine;
            string secondContent = "Cotton Sync Desktop live smoke pre-existing file from client B"
                + Environment.NewLine
                + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + Environment.NewLine;
            LiveSyncSmokeSeededLocalFile[] files = new LiveSyncSmokeSeededLocalFile[]
            {
                await WriteSeededLocalFileAsync(
                    startupOptions.LocalRoot!,
                    PreExistingClientAPath,
                    firstContent,
                    cancellationToken).ConfigureAwait(false),
                await WriteSeededLocalFileAsync(
                    startupOptions.SecondLocalRoot!,
                    PreExistingClientBPath,
                    secondContent,
                    cancellationToken).ConfigureAwait(false),
            };
            await output.WriteLineAsync(
                "Seeded pre-existing local files before sync pair creation: "
                + files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return files;
        }

        private static async Task<IReadOnlyList<LiveSyncSmokeSeededLocalFile>> SeedExistingLocalBurstAsync(
            DesktopStartupOptions startupOptions,
            int fileCount,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<LiveSyncSmokeSeedFile> plan = LiveSyncSmokeSeedPlan.Build(fileCount, DateTime.UtcNow);
            List<LiveSyncSmokeSeededLocalFile> files = new(plan.Count);
            foreach (LiveSyncSmokeSeedFile plannedFile in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string localRoot = plannedFile.UseFirstClient
                    ? startupOptions.LocalRoot!
                    : startupOptions.SecondLocalRoot!;
                files.Add(await WriteSeededLocalFileAsync(
                        localRoot,
                        plannedFile.RelativePath,
                        plannedFile.Content,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            await output.WriteLineAsync(
                "Seeded pre-existing local burst before sync pair creation: files="
                + files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", zeroByteFiles="
                + plan.Count(static file => file.Content.Length == 0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return files;
        }

        private static async Task<LiveSyncSmokeSeededLocalFile> WriteSeededLocalFileAsync(
            string localRoot,
            string relativePath,
            string content,
            CancellationToken cancellationToken)
        {
            string fullPath = FullPath(localRoot, relativePath);
            await WriteFileAsync(localRoot, relativePath, content, cancellationToken).ConfigureAwait(false);
            return new LiveSyncSmokeSeededLocalFile(
                fullPath,
                relativePath,
                await ComputeFileSha256Async(fullPath, cancellationToken).ConfigureAwait(false));
        }

        private static async Task<int> RunClientACreateAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string content = "Cotton Sync Desktop live smoke from client A" + Environment.NewLine
                + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine;
            await WriteFileAsync(startupOptions.LocalRoot!, LocalUploadPath, content, cancellationToken).ConfigureAwait(false);
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForPresentAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                LocalUploadPath,
                content,
                "Desktop local create uploaded and downloaded by the second client.",
                firstController,
                secondController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunClientBCreateAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string content = "Cotton Sync Desktop live smoke from client B" + Environment.NewLine
                + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine;
            await WriteFileAsync(startupOptions.SecondLocalRoot!, RemoteOriginPath, content, cancellationToken).ConfigureAwait(false);
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForPresentAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                RemoteOriginPath,
                content,
                "Desktop remote-origin create downloaded by the first client.",
                secondController,
                firstController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunClientARenameAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            const string label = "Desktop local rename propagated to the second client.";
            int materializationFailure = await EnsureRenameSourceReadableAsync(
                startupOptions.LocalRoot!,
                LocalUploadPath,
                label,
                output,
                cancellationToken).ConfigureAwait(false);
            if (materializationFailure != 0)
            {
                return materializationFailure;
            }

            File.Move(FullPath(startupOptions.LocalRoot!, LocalUploadPath), FullPath(startupOptions.LocalRoot!, LocalRenamedPath));
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForRenameAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                LocalUploadPath,
                LocalRenamedPath,
                label,
                firstController,
                secondController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunClientBRenameAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            const string label = "Desktop remote-origin rename propagated to the first client.";
            int materializationFailure = await EnsureRenameSourceReadableAsync(
                startupOptions.SecondLocalRoot!,
                RemoteOriginPath,
                label,
                output,
                cancellationToken).ConfigureAwait(false);
            if (materializationFailure != 0)
            {
                return materializationFailure;
            }

            File.Move(
                FullPath(startupOptions.SecondLocalRoot!, RemoteOriginPath),
                FullPath(startupOptions.SecondLocalRoot!, RemoteRenamedPath));
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForRenameAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                RemoteOriginPath,
                RemoteRenamedPath,
                label,
                secondController,
                firstController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunClientADeleteAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(FullPath(startupOptions.LocalRoot!, LocalRenamedPath))
                || !File.Exists(FullPath(startupOptions.SecondLocalRoot!, LocalRenamedPath)))
            {
                output.WriteLine(FormatCheck(false, "Desktop local delete propagated to the second client.")
                    + " path=" + LocalRenamedPath
                    + ", prerequisite=missing");
                return 1;
            }

            File.Delete(FullPath(startupOptions.LocalRoot!, LocalRenamedPath));
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForAbsentAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                LocalRenamedPath,
                "Desktop local delete propagated to the second client.",
                firstController,
                secondController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunClientBDeleteAsync(
            DesktopStartupOptions startupOptions,
            DesktopShellController firstController,
            DesktopShellController secondController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(FullPath(startupOptions.LocalRoot!, RemoteRenamedPath))
                || !File.Exists(FullPath(startupOptions.SecondLocalRoot!, RemoteRenamedPath)))
            {
                output.WriteLine(FormatCheck(false, "Desktop remote-origin delete propagated to the first client.")
                    + " path=" + RemoteRenamedPath
                    + ", prerequisite=missing");
                return 1;
            }

            File.Delete(FullPath(startupOptions.SecondLocalRoot!, RemoteRenamedPath));
            await WaitForDesktopQuietWindowAsync(output, cancellationToken).ConfigureAwait(false);
            return await WaitForAbsentAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                RemoteRenamedPath,
                "Desktop remote-origin delete propagated to the first client.",
                secondController,
                firstController,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task RunSourceThenTargetAsync(
            DesktopShellController sourceController,
            DesktopShellController targetController,
            CancellationToken cancellationToken)
        {
            await sourceController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            await targetController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            await RunFinalConvergenceAsync(sourceController, targetController, cancellationToken).ConfigureAwait(false);
        }

        private static async Task RunFinalConvergenceAsync(
            DesktopShellController firstController,
            DesktopShellController secondController,
            CancellationToken cancellationToken)
        {
            for (int pass = 0; pass < FinalConvergencePasses; pass++)
            {
                await firstController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                await secondController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task WriteFileAsync(
            string localRoot,
            string relativePath,
            string content,
            CancellationToken cancellationToken)
        {
            string fullPath = FullPath(localRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WaitForDesktopQuietWindowAsync(
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await output.WriteLineAsync(
                "Waiting "
                + DesktopLocalQuietWindow.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " seconds for the desktop local-change quiet window.").ConfigureAwait(false);
            await Task.Delay(DesktopLocalQuietWindow, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> EnsureRenameSourceReadableAsync(
            string localRoot,
            string relativePath,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            TextReadSnapshot source = await TryReadAllTextForLiveSmokeAsync(
                FullPath(localRoot, relativePath),
                cancellationToken).ConfigureAwait(false);
            if (source.Exists && source.Read)
            {
                return 0;
            }

            output.WriteLine(
                FormatCheck(false, label)
                + " path=" + relativePath
                + ", prerequisite="
                + (source.Exists ? "unreadable" : "missing")
                + (source.Details.Length == 0 ? string.Empty : ", details=" + source.Details));
            return 1;
        }

        private static async Task TryRemoveLiveSmokeSyncPairAsync(
            DesktopShellController controller,
            SyncPairSettings syncPair,
            TextWriter output,
            string label)
        {
            try
            {
                await controller.RemoveSyncPairAsync(syncPair.Id, CancellationToken.None).ConfigureAwait(false);
                await output.WriteLineAsync(
                    "Removed "
                    + label
                    + " live-smoke sync pair: "
                    + syncPair.LocalRootPath).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await output.WriteLineAsync(
                    "Warning: failed to remove "
                    + label
                    + " live-smoke sync pair "
                    + syncPair.Id
                    + ": "
                    + CleanSingleLine(exception.Message)).ConfigureAwait(false);
            }
        }

        private static async Task TrySignOutAsync(
            DesktopShellController controller,
            TextWriter output,
            string label)
        {
            try
            {
                await controller.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
                await output.WriteLineAsync("Signed out " + label + " desktop client.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await output.WriteLineAsync(
                    "Warning: failed to sign out "
                    + label
                    + " desktop client: "
                    + CleanSingleLine(exception.Message)).ConfigureAwait(false);
            }
        }
    }
}
