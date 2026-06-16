// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Startup
{
    internal class DesktopStartupOptions
    {
        private DesktopStartupOptions(
            Uri? serverUrl,
            string? username,
            string? dataDirectory,
            bool startMinimizedToTray,
            bool runSelfTest,
            bool exportDiagnostics,
            bool cleanupCloudFiles,
            bool runWindowsVirtualFilesSmoke,
            bool runLiveSyncSmoke,
            bool printVersion,
            DesktopVisualSmokeScenario? visualSmokeScenario,
            TimeSpan windowsVirtualFilesSmokeHoldAfterPlaceholder,
            string? windowsVirtualFilesSmokePhase,
            TimeSpan liveSyncSmokeApprovalHold,
            string? localRoot,
            string? secondLocalRoot,
            string? remotePath,
            SyncPairMode syncMode,
            string? syncModeError)
        {
            ServerUrl = serverUrl;
            Username = username;
            DataDirectory = dataDirectory;
            StartMinimizedToTray = startMinimizedToTray;
            RunSelfTest = runSelfTest;
            ExportDiagnostics = exportDiagnostics;
            CleanupCloudFiles = cleanupCloudFiles;
            RunWindowsVirtualFilesSmoke = runWindowsVirtualFilesSmoke;
            RunLiveSyncSmoke = runLiveSyncSmoke;
            PrintVersion = printVersion;
            VisualSmokeScenario = visualSmokeScenario;
            WindowsVirtualFilesSmokeHoldAfterPlaceholder = windowsVirtualFilesSmokeHoldAfterPlaceholder;
            WindowsVirtualFilesSmokePhase = windowsVirtualFilesSmokePhase;
            LiveSyncSmokeApprovalHold = liveSyncSmokeApprovalHold;
            LocalRoot = localRoot;
            SecondLocalRoot = secondLocalRoot;
            RemotePath = remotePath;
            SyncMode = syncMode;
            SyncModeError = syncModeError;
        }

        public static DesktopStartupOptions Empty { get; } = new(
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            TimeSpan.Zero,
            null,
            TimeSpan.Zero,
            null,
            null,
            null,
            SyncPairMode.FullMirror,
            null);

        public Uri? ServerUrl { get; }

        public string? Username { get; }

        public string? DataDirectory { get; }

        public bool StartMinimizedToTray { get; }

        public bool RunSelfTest { get; }

        public bool ExportDiagnostics { get; }

        public bool CleanupCloudFiles { get; }

        public bool RunWindowsVirtualFilesSmoke { get; }

        public bool RunLiveSyncSmoke { get; }

        public bool PrintVersion { get; }

        public DesktopVisualSmokeScenario? VisualSmokeScenario { get; }

        public TimeSpan WindowsVirtualFilesSmokeHoldAfterPlaceholder { get; }

        public string? WindowsVirtualFilesSmokePhase { get; }

        public TimeSpan LiveSyncSmokeApprovalHold { get; }

        public string? LocalRoot { get; }

        public string? SecondLocalRoot { get; }

        public string? RemotePath { get; }

        public SyncPairMode SyncMode { get; }

        public string? SyncModeError { get; }

        public static DesktopStartupOptions Parse(IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(args);
            string? serverUrl = ReadOption(args, "--server-url") ?? ReadOption(args, "--server");
            string? username = ReadOption(args, "--username") ?? ReadOption(args, "--user");
            string? dataDirectory = ReadOption(args, "--data-dir") ?? ReadOption(args, "--data-directory");
            string? visualSmokeScenario = ReadOption(args, "--visual-smoke") ?? ReadOption(args, "--screenshot-state");
            string? windowsVirtualFilesSmokeHoldAfterPlaceholder =
                ReadOption(args, "--vfs-smoke-hold-after-placeholder-seconds");
            string? windowsVirtualFilesSmokePhase = ReadOption(args, "--vfs-smoke-phase");
            string? liveSyncSmokeApprovalHold = ReadOption(args, "--live-sync-smoke-approval-hold-seconds")
                ?? ReadOption(args, "--desktop-live-sync-smoke-approval-hold-seconds");
            string? localRoot = ReadOption(args, "--local-root");
            string? secondLocalRoot = ReadOption(args, "--second-local-root");
            string? remotePath = ReadOption(args, "--remote-path");
            string? syncMode = ReadOption(args, "--sync-mode") ?? ReadOption(args, "--materialization-mode");
            (SyncPairMode parsedSyncMode, string? syncModeError) = ParseSyncMode(syncMode);
            bool startMinimizedToTray = HasFlag(args, "--start-minimized")
                || HasFlag(args, "--minimized")
                || HasFlag(args, "--tray");
            bool runSelfTest = HasFlag(args, "--self-test")
                || HasFlag(args, "--smoke-test");
            bool exportDiagnostics = HasFlag(args, "--export-diagnostics")
                || HasFlag(args, "--diagnostics");
            bool cleanupCloudFiles = HasFlag(args, "--cleanup-cloud-files")
                || HasFlag(args, "--cleanup-sync-roots");
            bool runWindowsVirtualFilesSmoke = HasFlag(args, "--windows-virtual-files-smoke")
                || HasFlag(args, "--vfs-smoke");
            bool runLiveSyncSmoke = HasFlag(args, "--live-sync-smoke")
                || HasFlag(args, "--desktop-live-sync-smoke");
            bool printVersion = HasFlag(args, "--version")
                || HasFlag(args, "-v")
                || HasFlag(args, "version");
            return new DesktopStartupOptions(
                DesktopServerUrl.NormalizeOptional(serverUrl),
                NormalizeOptional(username),
                NormalizeOptional(dataDirectory),
                startMinimizedToTray,
                runSelfTest,
                exportDiagnostics,
                cleanupCloudFiles,
                runWindowsVirtualFilesSmoke,
                runLiveSyncSmoke,
                printVersion,
                ParseVisualSmokeScenario(visualSmokeScenario),
                ParseNonNegativeSeconds(windowsVirtualFilesSmokeHoldAfterPlaceholder),
                NormalizeOptional(windowsVirtualFilesSmokePhase),
                ParseNonNegativeSeconds(liveSyncSmokeApprovalHold),
                NormalizeOptional(localRoot),
                NormalizeOptional(secondLocalRoot),
                NormalizeOptional(remotePath),
                parsedSyncMode,
                syncModeError);
        }

        private static bool HasFlag(IReadOnlyList<string> args, string name)
        {
            return args.Any(argument => string.Equals(argument, name, StringComparison.Ordinal));
        }

        private static string? ReadOption(IReadOnlyList<string> args, string name)
        {
            for (int index = 0; index < args.Count; index++)
            {
                string current = args[index];
                if (string.Equals(current, name, StringComparison.Ordinal))
                {
                    return index + 1 < args.Count && !IsOptionName(args[index + 1]) ? args[index + 1] : null;
                }

                string prefix = name + "=";
                if (current.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return current[prefix.Length..];
                }
            }

            return null;
        }

        private static bool IsOptionName(string value)
        {
            return value.StartsWith("--", StringComparison.Ordinal);
        }

        private static string? NormalizeOptional(string? value)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        private static DesktopVisualSmokeScenario? ParseVisualSmokeScenario(string? value)
        {
            string? normalized = NormalizeOptional(value);
            if (normalized is null)
            {
                return null;
            }

            string enumName = normalized
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal);
            return Enum.TryParse(enumName, ignoreCase: true, out DesktopVisualSmokeScenario scenario)
                ? scenario
                : null;
        }

        private static (SyncPairMode Mode, string? Error) ParseSyncMode(string? value)
        {
            string? normalized = NormalizeOptional(value);
            if (normalized is null)
            {
                return (SyncPairMode.FullMirror, null);
            }

            return normalized.ToLowerInvariant() switch
            {
                "full-mirror" or "fullmirror" or "mirror" => (SyncPairMode.FullMirror, null),
                "windows-virtual-files" or "windowsvirtualfiles" or "virtual-files" or "vfs" =>
                    (SyncPairMode.WindowsVirtualFiles, null),
                _ => (SyncPairMode.FullMirror, "Unsupported sync mode: " + normalized + ". Use full-mirror or windows-virtual-files."),
            };
        }

        private static TimeSpan ParseNonNegativeSeconds(string? value)
        {
            string? normalized = NormalizeOptional(value);
            if (normalized is null)
            {
                return TimeSpan.Zero;
            }

            return int.TryParse(normalized, out int seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        }
    }
}
