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
        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
        }

        private static async Task<int> WritePassFailAsync(
            TextWriter output,
            bool passed,
            string label,
            string details)
        {
            await output.WriteLineAsync(FormatCheck(passed, label) + details).ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static bool IsRemoteOnlyPlaceholderState(SyncPlaceholderHydrationState state)
        {
            return state is SyncPlaceholderHydrationState.RemoteOnly
                or SyncPlaceholderHydrationState.Dehydrated;
        }

        private static string FormatStateSummary(SyncStateEntry? state)
        {
            if (state is null)
            {
                return "missing";
            }

            return state.Kind
                + "/"
                + state.PlaceholderHydrationState
                + "/"
                + (state.RemoteContentHash ?? "no-hash");
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string ToFullPath(string rootPath, string relativePath)
        {
            return Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string CleanSingleLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Operation could not be completed.";
            }

            return message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static string FormatAttributes(FileAttributes attributes)
        {
            const int RecallOnOpen = 0x00040000;
            const int Pinned = 0x00080000;
            const int Unpinned = 0x00100000;
            const int RecallOnDataAccess = 0x00400000;

            List<string> names = new();
            int raw = (int)attributes;
            foreach (FileAttributes known in Enum.GetValues<FileAttributes>())
            {
                if ((int)known == 0 || known == FileAttributes.Normal)
                {
                    continue;
                }

                if ((attributes & known) == known)
                {
                    names.Add(known.ToString());
                    raw &= ~(int)known;
                }
            }

            AddKnownCloudFilesAttribute(raw, RecallOnOpen, "RecallOnOpen", names, out raw);
            AddKnownCloudFilesAttribute(raw, Pinned, "Pinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, Unpinned, "Unpinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, RecallOnDataAccess, "RecallOnDataAccess", names, out raw);
            if (names.Count == 0)
            {
                names.Add(FileAttributes.Normal.ToString());
            }

            if (raw != 0)
            {
                names.Add("0x" + raw.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join("|", names)
                + " (raw=0x"
                + ((int)attributes).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + ")";
        }

        private static bool HasRecallOnDataAccess(FileAttributes attributes)
        {
            const int RecallOnDataAccess = 0x00400000;
            return (((int)attributes) & RecallOnDataAccess) == RecallOnDataAccess;
        }

        internal static bool HasPinned(FileAttributes attributes)
        {
            const int Pinned = 0x00080000;
            return (((int)attributes) & Pinned) == Pinned;
        }

        private static bool HasUnpinned(FileAttributes attributes)
        {
            const int Unpinned = 0x00100000;
            return (((int)attributes) & Unpinned) == Unpinned;
        }

        private static bool IsHydratedWithoutPin(FileAttributes attributes)
        {
            return !HasPinned(attributes)
                && !HasUnpinned(attributes)
                && !HasRecallOnDataAccess(attributes)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static void AddKnownCloudFilesAttribute(
            int raw,
            int flag,
            string name,
            List<string> names,
            out int remaining)
        {
            remaining = raw;
            if ((raw & flag) == flag)
            {
                names.Add(name);
                remaining &= ~flag;
            }
        }

        private static byte[] CreateLargeHydrationContent()
        {
            byte[] content = new byte[LargeHydrationSizeBytes];
            for (int index = 0; index < content.Length; index++)
            {
                content[index] = (byte)(((index * 31) + (index / 8191)) & 0xff);
            }

            return content;
        }

        private static async Task<string> ReadAllTextThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadAllBytesThroughExternalProcessAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        private static async Task<byte[]> ReadAllBytesThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            string base64 = await DesktopPowerShellFileReader.ReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$bytes=[System.IO.File]::ReadAllBytes($env:COTTON_SYNC_EXTERNAL_READ_PATH); "
                + "[Convert]::ToBase64String($bytes)",
                filePath,
                ExternalFileReadTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            return Convert.FromBase64String(base64.Trim());
        }

        private static async Task<FileContentHash> ReadFileHashThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                await using FileStream stream = File.OpenRead(filePath);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return new FileContentHash(stream.Length, Convert.ToHexStringLower(hash));
            }

            string output = await DesktopPowerShellFileReader.ReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$stream=[System.IO.File]::OpenRead($env:COTTON_SYNC_EXTERNAL_READ_PATH); "
                + "try { "
                + "$sha=[System.Security.Cryptography.SHA256]::Create(); "
                + "$hash=$sha.ComputeHash($stream); "
                + "$hex=([System.BitConverter]::ToString($hash)).Replace('-','').ToLowerInvariant(); "
                + "'{0}|{1}' -f $stream.Length,$hex "
                + "} finally { $stream.Dispose(); if ($sha) { $sha.Dispose(); } }",
                filePath,
                ExternalFileReadTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            string[] parts = output.Trim().Split('|', 2);
            if (parts.Length != 2
                || !long.TryParse(
                    parts[0],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long length))
            {
                throw new InvalidOperationException("External file hash helper returned an invalid response.");
            }

            return new FileContentHash(length, parts[1]);
        }

        private static bool HasIntermediateProgress(IReadOnlyList<SyncTransferProgress> progress)
        {
            return progress.Any(static item =>
                !item.IsCompleted
                && item.TotalBytes.HasValue
                && item.TransferredBytes > 0
                && item.TransferredBytes < item.TotalBytes.Value);
        }

        private static bool IsMonotonicProgress(IReadOnlyList<SyncTransferProgress> progress)
        {
            long previous = -1;
            foreach (SyncTransferProgress item in progress.Where(static value => value.Direction == SyncTransferDirection.Download))
            {
                if (item.TransferredBytes < previous)
                {
                    return false;
                }

                previous = item.TransferredBytes;
            }

            return true;
        }

        private static async Task<int> VerifyRunProgressCompletedFinalizingCloudFilesAsync(
            TextWriter output,
            IReadOnlyList<AppRunProgress> progress,
            string label)
        {
            int finalizingStartIndex = -1;
            int finalizingCompletedIndex = -1;
            int completedIndex = -1;
            for (int index = 0; index < progress.Count; index++)
            {
                AppRunProgress item = progress[index];
                if (item.Stage == SyncRunProgressStage.FinalizingCloudFiles && !item.IsCompleted && finalizingStartIndex < 0)
                {
                    finalizingStartIndex = index;
                }

                if (item.Stage == SyncRunProgressStage.FinalizingCloudFiles && item.IsCompleted)
                {
                    finalizingCompletedIndex = index;
                }

                if (item.Stage == SyncRunProgressStage.Completed)
                {
                    completedIndex = index;
                }
            }

            bool passed = finalizingStartIndex >= 0
                && finalizingCompletedIndex > finalizingStartIndex
                && (completedIndex < 0 || finalizingCompletedIndex > completedIndex);
            await output.WriteLineAsync(
                FormatCheck(passed, "Cloud Files finalization progress completed before smoke success for " + label + ".")
                + " samples="
                + progress.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", finalizingStartIndex="
                + finalizingStartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", finalizingCompletedIndex="
                + finalizingCompletedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", completedIndex="
                + completedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static async Task<bool> WaitForAttributesAsync(
            string filePath,
            Func<FileAttributes, bool> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.Exists(filePath) || Directory.Exists(filePath))
                    && predicate(File.GetAttributes(filePath)))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return (File.Exists(filePath) || Directory.Exists(filePath))
                && predicate(File.GetAttributes(filePath));
        }
    }
}
