// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
            bool exportPrivateSupportDiagnostics,
            bool cleanupCloudFiles,
            bool runWindowsVirtualFilesSmoke,
            bool runLiveSyncSmoke,
            bool runUpdateDiscoverySmoke,
            bool runUpdateInstallSmoke,
            bool runShellShareLinkSmoke,
            bool runSocketCleanupSmoke,
            bool printVersion,
            string? shellShareLinkTargetPath,
            string? shellCopyShareLinkTargetPath,
            DesktopVisualSmokeScenario? visualSmokeScenario,
            Uri? updateManifestUri,
            string? expectedUpdateVersion,
            string? updateInstallerPath,
            TimeSpan windowsVirtualFilesSmokeHoldAfterPlaceholder,
            string? windowsVirtualFilesSmokePhase,
            int? windowsVirtualFilesSmokePlaceholderCount,
            TimeSpan liveSyncSmokeApprovalHold,
            bool liveSyncSmokePreserveExistingLocalFiles,
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
            ExportPrivateSupportDiagnostics = exportPrivateSupportDiagnostics;
            CleanupCloudFiles = cleanupCloudFiles;
            RunWindowsVirtualFilesSmoke = runWindowsVirtualFilesSmoke;
            RunLiveSyncSmoke = runLiveSyncSmoke;
            RunUpdateDiscoverySmoke = runUpdateDiscoverySmoke;
            RunUpdateInstallSmoke = runUpdateInstallSmoke;
            RunShellShareLinkSmoke = runShellShareLinkSmoke;
            RunSocketCleanupSmoke = runSocketCleanupSmoke;
            PrintVersion = printVersion;
            ShellShareLinkTargetPath = shellShareLinkTargetPath;
            ShellCopyShareLinkTargetPath = shellCopyShareLinkTargetPath;
            VisualSmokeScenario = visualSmokeScenario;
            UpdateManifestUri = updateManifestUri;
            ExpectedUpdateVersion = expectedUpdateVersion;
            UpdateInstallerPath = updateInstallerPath;
            WindowsVirtualFilesSmokeHoldAfterPlaceholder = windowsVirtualFilesSmokeHoldAfterPlaceholder;
            WindowsVirtualFilesSmokePhase = windowsVirtualFilesSmokePhase;
            WindowsVirtualFilesSmokePlaceholderCount = windowsVirtualFilesSmokePlaceholderCount;
            LiveSyncSmokeApprovalHold = liveSyncSmokeApprovalHold;
            LiveSyncSmokePreserveExistingLocalFiles = liveSyncSmokePreserveExistingLocalFiles;
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
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            TimeSpan.Zero,
            null,
            null,
            TimeSpan.Zero,
            false,
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

        public bool ExportPrivateSupportDiagnostics { get; }

        public bool CleanupCloudFiles { get; }

        public bool RunWindowsVirtualFilesSmoke { get; }

        public bool RunLiveSyncSmoke { get; }

        public bool RunUpdateDiscoverySmoke { get; }

        public bool RunUpdateInstallSmoke { get; }

        public bool RunShellShareLinkSmoke { get; }

        public bool RunSocketCleanupSmoke { get; }

        public bool PrintVersion { get; }

        public string? ShellShareLinkTargetPath { get; }

        public string? ShellCopyShareLinkTargetPath { get; }

        public DesktopVisualSmokeScenario? VisualSmokeScenario { get; }

        public Uri? UpdateManifestUri { get; }

        public string? ExpectedUpdateVersion { get; }

        public string? UpdateInstallerPath { get; }

        public TimeSpan WindowsVirtualFilesSmokeHoldAfterPlaceholder { get; }

        public string? WindowsVirtualFilesSmokePhase { get; }

        public int? WindowsVirtualFilesSmokePlaceholderCount { get; }

        public TimeSpan LiveSyncSmokeApprovalHold { get; }

        public bool LiveSyncSmokePreserveExistingLocalFiles { get; }

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
            string? windowsVirtualFilesSmokePlaceholderCount =
                ReadOption(args, "--vfs-smoke-placeholder-count") ?? ReadOption(args, "--vfs-smoke-file-count");
            string? liveSyncSmokeApprovalHold = ReadOption(args, "--live-sync-smoke-approval-hold-seconds")
                ?? ReadOption(args, "--desktop-live-sync-smoke-approval-hold-seconds");
            string? localRoot = ReadOption(args, "--local-root");
            string? secondLocalRoot = ReadOption(args, "--second-local-root");
            string? remotePath = ReadOption(args, "--remote-path");
            string? syncMode = ReadOption(args, "--sync-mode") ?? ReadOption(args, "--materialization-mode");
            string? updateManifestUri = ReadOption(args, "--update-manifest-url")
                ?? ReadOption(args, "--update-manifest-uri");
            string? expectedUpdateVersion = ReadOption(args, "--expected-update-version")
                ?? ReadOption(args, "--expected-latest-version");
            string? updateInstallerPath = ReadOption(args, "--update-installer-path")
                ?? ReadOption(args, "--installer-path");
            string? shellShareLinkTargetPath = ReadOption(args, "--resolve-shell-share-link-target")
                ?? ReadOption(args, "--shell-share-link-target");
            string? shellCopyShareLinkTargetPath = ReadOption(args, "--copy-shell-share-link")
                ?? ReadOption(args, "--copy-shell-share-link-target");
            (SyncPairMode parsedSyncMode, string? syncModeError) = ParseSyncMode(syncMode);
            bool startMinimizedToTray = HasFlag(args, "--start-minimized")
                || HasFlag(args, "--minimized")
                || HasFlag(args, "--tray");
            bool runSelfTest = HasFlag(args, "--self-test")
                || HasFlag(args, "--smoke-test");
            bool exportPrivateSupportDiagnostics = HasFlag(args, "--export-diagnostics-private")
                || HasFlag(args, "--include-private-diagnostics")
                || HasFlag(args, "--private-support-diagnostics");
            bool exportDiagnostics = HasFlag(args, "--export-diagnostics")
                || HasFlag(args, "--diagnostics")
                || exportPrivateSupportDiagnostics;
            bool cleanupCloudFiles = HasFlag(args, "--cleanup-cloud-files")
                || HasFlag(args, "--cleanup-sync-roots");
            bool runWindowsVirtualFilesSmoke = HasFlag(args, "--windows-virtual-files-smoke")
                || HasFlag(args, "--vfs-smoke");
            bool runLiveSyncSmoke = HasFlag(args, "--live-sync-smoke")
                || HasFlag(args, "--desktop-live-sync-smoke");
            bool runUpdateDiscoverySmoke = HasFlag(args, "--update-discovery-smoke")
                || HasFlag(args, "--desktop-update-smoke");
            bool runUpdateInstallSmoke = HasFlag(args, "--update-install-smoke")
                || HasFlag(args, "--desktop-update-install-smoke");
            bool runShellShareLinkSmoke = HasFlag(args, "--shell-share-link-smoke")
                || HasFlag(args, "--desktop-shell-share-link-smoke");
            bool runSocketCleanupSmoke = HasFlag(args, "--socket-cleanup-smoke")
                || HasFlag(args, "--desktop-socket-cleanup-smoke");
            bool liveSyncSmokePreserveExistingLocalFiles =
                HasFlag(args, "--live-sync-smoke-preserve-existing-local-files");
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
                exportPrivateSupportDiagnostics,
                cleanupCloudFiles,
                runWindowsVirtualFilesSmoke,
                runLiveSyncSmoke,
                runUpdateDiscoverySmoke,
                runUpdateInstallSmoke,
                runShellShareLinkSmoke,
                runSocketCleanupSmoke,
                printVersion,
                NormalizeOptional(shellShareLinkTargetPath),
                NormalizeOptional(shellCopyShareLinkTargetPath),
                ParseVisualSmokeScenario(visualSmokeScenario),
                ParseAbsoluteUri(updateManifestUri),
                NormalizeOptional(expectedUpdateVersion),
                NormalizeOptional(updateInstallerPath),
                ParseNonNegativeSeconds(windowsVirtualFilesSmokeHoldAfterPlaceholder),
                NormalizeOptional(windowsVirtualFilesSmokePhase),
                ParsePositiveInt32(windowsVirtualFilesSmokePlaceholderCount),
                ParseNonNegativeSeconds(liveSyncSmokeApprovalHold),
                liveSyncSmokePreserveExistingLocalFiles,
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

        private static Uri? ParseAbsoluteUri(string? value)
        {
            string? normalized = NormalizeOptional(value);
            return normalized is not null && Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
                ? uri
                : null;
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

        private static int? ParsePositiveInt32(string? value)
        {
            string? normalized = NormalizeOptional(value);
            if (normalized is null)
            {
                return null;
            }

            return int.TryParse(normalized, out int parsed) && parsed > 0
                ? parsed
                : null;
        }
    }
}
