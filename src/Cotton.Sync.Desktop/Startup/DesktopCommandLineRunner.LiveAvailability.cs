// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<int> RunLiveAvailabilityAndRestoreAsync(
            DesktopStartupOptions options,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            string content = "Persisted client session and virtual-file availability probe.";
            await WriteFileAsync(options.SecondLocalRoot!, LocalUploadPath, content, cancellationToken).ConfigureAwait(false);
            failures += await WaitForAutomaticContentAsync(options, session, content,
                "Availability probe reached both clients automatically.", output, cancellationToken).ConfigureAwait(false);
            if (failures != 0)
            {
                return failures;
            }

            if (options.SyncMode == SyncPairMode.WindowsVirtualFiles)
            {
                WindowsCloudFilesNativeApi native = new();
                WindowsCloudFilesAdapter adapter = new(nativeApi: native);
                string fullPath = FullPath(options.LocalRoot!, LocalUploadPath);
                native.SetPinState(fullPath, WindowsCloudFilesPinState.Unpinned);
                adapter.DehydratePlaceholder(session.FirstPair!, LocalUploadPath);
                bool onlineOnly = HasLiveRecallAttributes(File.GetAttributes(fullPath));
                await output.WriteLineAsync(FormatCheck(onlineOnly,
                    "Live file became online-only after freeing space.")).ConfigureAwait(false);
                if (!onlineOnly)
                {
                    failures++;
                }

                TextReadSnapshot read = await TryReadAllTextForLiveSmokeAsync(fullPath, cancellationToken).ConfigureAwait(false);
                bool hydrated = read.Read && read.Content == content && !HasLiveRecallAttributes(File.GetAttributes(fullPath));
                await output.WriteLineAsync(FormatCheck(hydrated,
                    "External file read hydrated exact bytes from the live server.")).ConfigureAwait(false);
                if (!hydrated)
                {
                    failures++;
                }

                adapter.PinPlaceholder(session.FirstPair!, LocalUploadPath);
                bool pinned = (File.GetAttributes(fullPath) & (FileAttributes)0x00080000) != 0;
                await output.WriteLineAsync(FormatCheck(pinned,
                    "Always-keep pin state is set on the live file.")).ConfigureAwait(false);
                if (!pinned)
                {
                    failures++;
                }
            }

            await session.FirstController.DisposeAsync().ConfigureAwait(false);
            DesktopShellController restored = CreateLiveSmokeController(session.FirstPaths, options, output);
            session.ReplaceFirstController(restored);
            DesktopShellSnapshot snapshot = await restored.LoadAsync(cancellationToken).ConfigureAwait(false);
            bool signedIn = snapshot.IsSignedIn && snapshot.SyncPairs.Any(pair => pair.Id == session.FirstPair!.Id);
            await output.WriteLineAsync(FormatCheck(signedIn,
                "Recreated client restored its saved HTTP session and existing sync pair without a new sign-in.")).ConfigureAwait(false);
            if (!signedIn)
            {
                return failures + 1;
            }

            failures += await WaitForAutomaticContentAsync(options, session, content,
                "Restored client kept exact local bytes and returned to idle.", output, cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static bool HasLiveRecallAttributes(FileAttributes attributes)
        {
            const FileAttributes recall = (FileAttributes)(0x00400000 | 0x00040000);
            return (attributes & (FileAttributes.Offline | recall)) != 0;
        }
    }
}
