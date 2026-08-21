// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal class LiveSmokePlatformCommandService(TextWriter output, TimeSpan approvalHold)
        : IPlatformCommandService
    {
        public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return output.WriteLineAsync("Open folder skipped by live sync smoke: " + localPath);
        }

        public async Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync("Approval URL: " + url.AbsoluteUri).ConfigureAwait(false);
            await output.WriteLineAsync("Open this URL in your browser to approve sign-in.").ConfigureAwait(false);
            if (approvalHold > TimeSpan.Zero)
            {
                await output.WriteLineAsync(
                    "Holding "
                    + approvalHold.TotalSeconds.ToString(
                        "0.###",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds before polling so the approval page can load.").ConfigureAwait(false);
                await Task.Delay(approvalHold, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync("Waiting for browser approval...").ConfigureAwait(false);
        }
    }
}
