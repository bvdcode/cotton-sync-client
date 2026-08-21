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
        private static async Task<int> WaitForPresentAsync(
            string firstLocalRoot,
            string secondLocalRoot,
            string relativePath,
            string expectedContent,
            string label,
            DesktopShellController sourceController,
            DesktopShellController targetController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedContent)));
            DateTime deadlineUtc = DateTime.UtcNow + PropagationTimeout;
            int attempts = 0;
            PresenceSnapshot snapshot;
            do
            {
                attempts++;
                await RunSourceThenTargetAsync(sourceController, targetController, cancellationToken).ConfigureAwait(false);
                snapshot = await CapturePresenceAsync(
                    firstLocalRoot,
                    secondLocalRoot,
                    relativePath,
                    expectedContent,
                    cancellationToken).ConfigureAwait(false);
                if (snapshot.Passed)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, label)
                        + " path=" + relativePath
                        + ", sha256=" + hash
                        + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return 0;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    break;
                }

                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);

            await output.WriteLineAsync(
                FormatCheck(false, label)
                + " path=" + relativePath
                + ", sha256=" + hash
                + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", " + snapshot.Details).ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> WaitForRenameAsync(
            string firstLocalRoot,
            string secondLocalRoot,
            string oldPath,
            string newPath,
            string label,
            DesktopShellController sourceController,
            DesktopShellController targetController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow + PropagationTimeout;
            int attempts = 0;
            RenameSnapshot snapshot;
            do
            {
                attempts++;
                await RunSourceThenTargetAsync(sourceController, targetController, cancellationToken).ConfigureAwait(false);
                snapshot = CaptureRename(firstLocalRoot, secondLocalRoot, oldPath, newPath);
                if (snapshot.Passed)
                {
                    output.WriteLine(FormatCheck(true, label)
                        + " oldPath=" + oldPath
                        + ", newPath=" + newPath
                        + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return 0;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    break;
                }

                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);

            output.WriteLine(FormatCheck(false, label)
                + " oldPath=" + oldPath
                + ", newPath=" + newPath
                + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", " + snapshot.Details);
            return 1;
        }

        private static async Task<int> WaitForAbsentAsync(
            string firstLocalRoot,
            string secondLocalRoot,
            string relativePath,
            string label,
            DesktopShellController sourceController,
            DesktopShellController targetController,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow + PropagationTimeout;
            int attempts = 0;
            AbsentSnapshot snapshot;
            do
            {
                attempts++;
                await RunSourceThenTargetAsync(sourceController, targetController, cancellationToken).ConfigureAwait(false);
                snapshot = CaptureAbsent(firstLocalRoot, secondLocalRoot, relativePath);
                if (snapshot.Passed)
                {
                    output.WriteLine(FormatCheck(true, label)
                        + " path=" + relativePath
                        + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return 0;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    break;
                }

                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);

            output.WriteLine(FormatCheck(false, label)
                + " path=" + relativePath
                + ", attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", " + snapshot.Details);
            return 1;
        }

        private static async Task<PresenceSnapshot> CapturePresenceAsync(
            string firstLocalRoot,
            string secondLocalRoot,
            string relativePath,
            string expectedContent,
            CancellationToken cancellationToken)
        {
            string firstPath = FullPath(firstLocalRoot, relativePath);
            string secondPath = FullPath(secondLocalRoot, relativePath);
            TextReadSnapshot first = await TryReadAllTextForLiveSmokeAsync(firstPath, cancellationToken)
                .ConfigureAwait(false);
            TextReadSnapshot second = await TryReadAllTextForLiveSmokeAsync(secondPath, cancellationToken)
                .ConfigureAwait(false);
            bool firstMatches = string.Equals(first.Content, expectedContent, StringComparison.Ordinal);
            bool secondMatches = string.Equals(second.Content, expectedContent, StringComparison.Ordinal);
            bool passed = first.Exists && second.Exists && first.Read && second.Read && firstMatches && secondMatches;
            return new PresenceSnapshot(
                passed,
                "firstExists=" + first.Exists
                + ", secondExists=" + second.Exists
                + ", firstRead=" + first.Read
                + ", secondRead=" + second.Read
                + ", firstMatches=" + firstMatches
                + ", secondMatches=" + secondMatches
                + (first.Details.Length == 0 ? string.Empty : ", firstDetails=" + first.Details)
                + (second.Details.Length == 0 ? string.Empty : ", secondDetails=" + second.Details));
        }

        private static async Task<string> ComputeFileSha256Async(
            string filePath,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }

        private static RenameSnapshot CaptureRename(
            string firstLocalRoot,
            string secondLocalRoot,
            string oldPath,
            string newPath)
        {
            bool firstOldExists = File.Exists(FullPath(firstLocalRoot, oldPath));
            bool secondOldExists = File.Exists(FullPath(secondLocalRoot, oldPath));
            bool firstNewExists = File.Exists(FullPath(firstLocalRoot, newPath));
            bool secondNewExists = File.Exists(FullPath(secondLocalRoot, newPath));
            bool passed = !firstOldExists && !secondOldExists && firstNewExists && secondNewExists;
            return new RenameSnapshot(
                passed,
                "firstOldExists=" + firstOldExists
                + ", secondOldExists=" + secondOldExists
                + ", firstNewExists=" + firstNewExists
                + ", secondNewExists=" + secondNewExists);
        }

        private static AbsentSnapshot CaptureAbsent(
            string firstLocalRoot,
            string secondLocalRoot,
            string relativePath)
        {
            bool firstExists = File.Exists(FullPath(firstLocalRoot, relativePath));
            bool secondExists = File.Exists(FullPath(secondLocalRoot, relativePath));
            return new AbsentSnapshot(
                !firstExists && !secondExists,
                "firstExists=" + firstExists + ", secondExists=" + secondExists);
        }
    }
}
