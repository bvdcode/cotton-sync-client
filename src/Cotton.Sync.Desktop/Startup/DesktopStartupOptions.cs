// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Startup
{
    internal class DesktopStartupOptions
    {
        private DesktopStartupOptions()
        {
        }

        public static DesktopStartupOptions Empty { get; } = new();

        public Uri? ServerUrl { get; private init; }

        public string? Username { get; private init; }

        public string? DataDirectory { get; private init; }

        public bool StartMinimizedToTray { get; private init; }

        public bool RunSelfTest { get; private init; }

        public bool ExportDiagnostics { get; private init; }

        public bool ExportPrivateSupportDiagnostics { get; private init; }

        public bool CleanupCloudFiles { get; private init; }

        public bool RunWindowsVirtualFilesSmoke { get; private init; }

        public bool RunLiveSyncSmoke { get; private init; }

        public bool RunUpdateDiscoverySmoke { get; private init; }

        public bool RunUpdateInstallSmoke { get; private init; }

        public bool RunShellShareLinkSmoke { get; private init; }

        public bool RunSocketCleanupSmoke { get; private init; }

        public bool PrintVersion { get; private init; }

        public string? ShellShareLinkTargetPath { get; private init; }

        public string? ShellCopyShareLinkTargetPath { get; private init; }

        public DesktopVisualSmokeScenario? VisualSmokeScenario { get; private init; }

        public double? VisualSmokeScale { get; private init; }

        public Uri? UpdateManifestUri { get; private init; }

        public string? ExpectedUpdateVersion { get; private init; }

        public string? UpdateInstallerPath { get; private init; }

        public TimeSpan WindowsVirtualFilesSmokeHoldAfterPlaceholder { get; private init; }

        public string? WindowsVirtualFilesSmokePhase { get; private init; }

        public int? WindowsVirtualFilesSmokePlaceholderCount { get; private init; }

        public TimeSpan LiveSyncSmokeApprovalHold { get; private init; }

        public bool LiveSyncSmokePreserveExistingLocalFiles { get; private init; }

        public int? LiveSyncSmokeSeedFileCount { get; private init; }

        public string? LocalRoot { get; private init; }

        public string? SecondLocalRoot { get; private init; }

        public string? RemotePath { get; private init; }

        public SyncPairMode SyncMode { get; private init; } = SyncPairMode.FullMirror;

        public string? SyncModeError { get; private init; }

        public static DesktopStartupOptions Parse(IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(args);
            string? visualSmokeScenario = ReadFirstOption(args, "--visual-smoke", "--screenshot-state");
            DesktopVisualSmokeScenario? parsedVisualSmokeScenario = ParseVisualSmokeScenario(visualSmokeScenario);
            bool exportPrivateDiagnostics = HasAnyFlag(
                args,
                "--export-diagnostics-private",
                "--include-private-diagnostics",
                "--private-support-diagnostics");
            (SyncPairMode syncMode, string? syncModeError) = ParseSyncMode(
                ReadFirstOption(args, "--sync-mode", "--materialization-mode"));
            return new DesktopStartupOptions
            {
                CleanupCloudFiles = HasAnyFlag(args, "--cleanup-cloud-files", "--cleanup-sync-roots"),
                DataDirectory = NormalizeOptional(ReadFirstOption(args, "--data-dir", "--data-directory")),
                ExpectedUpdateVersion = NormalizeOptional(
                    ReadFirstOption(args, "--expected-update-version", "--expected-latest-version")),
                ExportDiagnostics = exportPrivateDiagnostics
                    || HasAnyFlag(args, "--export-diagnostics", "--diagnostics"),
                ExportPrivateSupportDiagnostics = exportPrivateDiagnostics,
                LiveSyncSmokeApprovalHold = ParseNonNegativeSeconds(ReadFirstOption(
                    args,
                    "--live-sync-smoke-approval-hold-seconds",
                    "--desktop-live-sync-smoke-approval-hold-seconds")),
                LiveSyncSmokePreserveExistingLocalFiles = HasFlag(
                    args,
                    "--live-sync-smoke-preserve-existing-local-files"),
                LiveSyncSmokeSeedFileCount = ParsePositiveInt32(
                    ReadOption(args, "--live-sync-smoke-seed-file-count")),
                LocalRoot = NormalizeOptional(ReadOption(args, "--local-root")),
                PrintVersion = HasAnyFlag(args, "--version", "-v", "version"),
                RemotePath = NormalizeOptional(ReadOption(args, "--remote-path")),
                RunLiveSyncSmoke = HasAnyFlag(args, "--live-sync-smoke", "--desktop-live-sync-smoke"),
                RunSelfTest = HasAnyFlag(args, "--self-test", "--smoke-test"),
                RunShellShareLinkSmoke = HasAnyFlag(
                    args,
                    "--shell-share-link-smoke",
                    "--desktop-shell-share-link-smoke"),
                RunSocketCleanupSmoke = HasAnyFlag(
                    args,
                    "--socket-cleanup-smoke",
                    "--desktop-socket-cleanup-smoke"),
                RunUpdateDiscoverySmoke = HasAnyFlag(
                    args,
                    "--update-discovery-smoke",
                    "--desktop-update-smoke"),
                RunUpdateInstallSmoke = HasAnyFlag(
                    args,
                    "--update-install-smoke",
                    "--desktop-update-install-smoke"),
                RunWindowsVirtualFilesSmoke = HasAnyFlag(
                    args,
                    "--windows-virtual-files-smoke",
                    "--vfs-smoke"),
                SecondLocalRoot = NormalizeOptional(ReadOption(args, "--second-local-root")),
                ServerUrl = DesktopServerUrl.NormalizeOptional(ReadFirstOption(args, "--server-url", "--server")),
                ShellCopyShareLinkTargetPath = NormalizeOptional(
                    ReadFirstOption(args, "--copy-shell-share-link", "--copy-shell-share-link-target")),
                ShellShareLinkTargetPath = NormalizeOptional(
                    ReadFirstOption(args, "--resolve-shell-share-link-target", "--shell-share-link-target")),
                StartMinimizedToTray = HasAnyFlag(args, "--start-minimized", "--minimized", "--tray"),
                SyncMode = syncMode,
                SyncModeError = syncModeError,
                UpdateInstallerPath = NormalizeOptional(
                    ReadFirstOption(args, "--update-installer-path", "--installer-path")),
                UpdateManifestUri = ParseAbsoluteUri(
                    ReadFirstOption(args, "--update-manifest-url", "--update-manifest-uri")),
                Username = NormalizeOptional(ReadFirstOption(args, "--username", "--user")),
                VisualSmokeScale = ParseVisualSmokeScale(
                    ReadOption(args, "--visual-scale"),
                    parsedVisualSmokeScenario),
                VisualSmokeScenario = parsedVisualSmokeScenario,
                WindowsVirtualFilesSmokeHoldAfterPlaceholder = ParseNonNegativeSeconds(
                    ReadOption(args, "--vfs-smoke-hold-after-placeholder-seconds")),
                WindowsVirtualFilesSmokePhase = NormalizeOptional(ReadOption(args, "--vfs-smoke-phase")),
                WindowsVirtualFilesSmokePlaceholderCount = ParsePositiveInt32(
                    ReadFirstOption(args, "--vfs-smoke-placeholder-count", "--vfs-smoke-file-count")),
            };
        }

        private static bool HasAnyFlag(IReadOnlyList<string> args, params string[] names)
        {
            return names.Any(name => HasFlag(args, name));
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

        private static string? ReadFirstOption(IReadOnlyList<string> args, params string[] names)
        {
            foreach (string name in names)
            {
                string? value = ReadOption(args, name);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        private static double? ParseVisualSmokeScale(
            string? value,
            DesktopVisualSmokeScenario? visualSmokeScenario)
        {
            string? normalized = NormalizeOptional(value);
            return visualSmokeScenario is not null
                && double.TryParse(
                    normalized,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double scale)
                && scale is >= 1 and <= 3
                    ? scale
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
