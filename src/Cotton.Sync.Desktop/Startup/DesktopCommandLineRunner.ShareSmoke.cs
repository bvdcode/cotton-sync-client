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
        public static async Task<int> RunShellShareLinkSmokeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunShellShareLinkSmokeAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<int> RunShellShareLinkSmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            if (startupOptions.DataDirectory is null)
            {
                await output.WriteLineAsync("Cotton Sync Desktop shell share-link smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output
                    .WriteLineAsync("--shell-share-link-smoke requires an explicit --data-dir so test state never uses the real user profile.")
                    .ConfigureAwait(false);
                return 2;
            }

            Directory.CreateDirectory(paths.DataDirectory);
            DesktopTraceLogging.Install(paths);
            await RememberShellShareLinkSmokeServerAsync(paths, startupOptions.ServerUrl, cancellationToken)
                .ConfigureAwait(false);
            ShellShareLinkSmokeData smokeData = await PrepareShellShareLinkSmokeDataAsync(paths, cancellationToken)
                .ConfigureAwait(false);

            await output.WriteLineAsync("Cotton Sync Desktop shell share-link smoke").ConfigureAwait(false);
            int failures = 0;
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "State-backed file share link copied",
                smokeData.SyncedFilePath,
                DesktopShellShareLinkResult.Created(new Uri("https://share.example/s/file")),
                expectCopied: true,
                expectedFailureReason: null,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "State-backed remote-only placeholder share link copied",
                smokeData.RemoteOnlyPlaceholderPath,
                DesktopShellShareLinkResult.Created(new Uri("https://share.example/s/remote-only")),
                expectCopied: true,
                expectedFailureReason: null,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "State-backed hydrated placeholder share link copied",
                smokeData.HydratedPlaceholderPath,
                DesktopShellShareLinkResult.Created(new Uri("https://share.example/s/hydrated")),
                expectCopied: true,
                expectedFailureReason: null,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "State-backed folder share link copied",
                smokeData.DirectoryPath,
                DesktopShellShareLinkResult.Created(new Uri("https://share.example/s/folder")),
                expectCopied: true,
                expectedFailureReason: null,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "Local-only item is rejected without clipboard write",
                smokeData.LocalOnlyPath,
                DesktopShellShareLinkResult.Unavailable("target-not-shareable"),
                expectCopied: false,
                expectedFailureReason: "target-missing-baseline",
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunShellShareLinkSmokeCopyCaseAsync(
                paths,
                "Signed-out share link target asks for sign-in",
                smokeData.SyncedFilePath,
                DesktopShellShareLinkResult.Failed("auth-token-missing"),
                expectCopied: false,
                expectedFailureReason: "auth-token-missing",
                output,
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task RememberShellShareLinkSmokeServerAsync(
            DesktopAppPaths paths,
            Uri? serverUrl,
            CancellationToken cancellationToken)
        {
            if (serverUrl is null)
            {
                return;
            }

            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.RememberedServerUrl = serverUrl;
            await preferencesStore.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<(ShellShareLinkTarget Target, DesktopShellShareLinkResult ShareLinkResult)>
            ResolveShellShareLinkAsync(
                DesktopAppPaths paths,
                DesktopStartupOptions startupOptions,
                string selectedPath,
                IShellShareLinkTargetResolver? resolver,
                IDesktopShellShareLinkClient? shareLinkClient,
                CancellationToken cancellationToken)
        {
            IShellShareLinkTargetResolver targetResolver = resolver
                ?? new ShellShareLinkTargetResolver(
                    new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                    new SqliteSyncStateStore(paths.SyncStateDatabasePath));
            ShellShareLinkTarget target = await targetResolver.ResolveAsync(selectedPath, cancellationToken)
                .ConfigureAwait(false);
            DesktopShellShareLinkResult shareLinkResult = target.CanCreateShareLink
                ? await CreateShellShareLinkAsync(paths, startupOptions, target, shareLinkClient, cancellationToken)
                    .ConfigureAwait(false)
                : DesktopShellShareLinkResult.Unavailable("target-not-shareable");
            return (target, shareLinkResult);
        }

        private static async Task<ShellShareLinkSmokeData> PrepareShellShareLinkSmokeDataAsync(
            DesktopAppPaths paths,
            CancellationToken cancellationToken)
        {
            string localRoot = Path.Combine(paths.DataDirectory, "shell-share-link-root");
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(Path.Combine(localRoot, "Folder"));
            await File.WriteAllTextAsync(Path.Combine(localRoot, "synced-file.txt"), "synced file", cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "remote-only-placeholder.txt"), "remote-only placeholder", cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "hydrated-placeholder.txt"), "hydrated placeholder", cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "local-only.txt"), "local only", cancellationToken)
                .ConfigureAwait(false);

            SyncPairSettings syncPair = new SyncPairSettings
            {
                Id = ShellShareLinkSmokePairId,
                DisplayName = "Cloud",
                LocalRootPath = localRoot,
                RemoteRootNodeId = Guid.CreateVersion7(),
                RemoteDisplayPath = "/Cloud",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };

            SqliteSyncPairSettingsStore pairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await pairStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await pairStore.UpsertAsync(syncPair, cancellationToken).ConfigureAwait(false);

            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await stateStore.UpsertAsync(CreateShellShareLinkSmokeState(syncPair, "synced-file.txt", SyncEntryKind.File), cancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(CreateShellShareLinkSmokeState(syncPair, "remote-only-placeholder.txt", SyncEntryKind.File), cancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(CreateShellShareLinkSmokeState(syncPair, "hydrated-placeholder.txt", SyncEntryKind.File), cancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(CreateShellShareLinkSmokeState(syncPair, "Folder", SyncEntryKind.Directory), cancellationToken)
                .ConfigureAwait(false);

            return new ShellShareLinkSmokeData(
                Path.Combine(localRoot, "synced-file.txt"),
                Path.Combine(localRoot, "remote-only-placeholder.txt"),
                Path.Combine(localRoot, "hydrated-placeholder.txt"),
                Path.Combine(localRoot, "Folder"),
                Path.Combine(localRoot, "local-only.txt"));
        }

        private static SyncStateEntry CreateShellShareLinkSmokeState(
            SyncPairSettings syncPair,
            string relativePath,
            SyncEntryKind kind)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = kind,
                RemoteNodeId = Guid.CreateVersion7(),
                RemoteFileId = kind == SyncEntryKind.File ? Guid.CreateVersion7() : null,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static async Task<int> RunShellShareLinkSmokeCopyCaseAsync(
            DesktopAppPaths paths,
            string label,
            string selectedPath,
            DesktopShellShareLinkResult shareLinkResult,
            bool expectCopied,
            string? expectedFailureReason,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [
                    "--data-dir",
                    paths.DataDirectory,
                    "--copy-shell-share-link",
                    selectedPath,
                ]);
            using StringWriter caseOutput = new StringWriter();
            ShellShareLinkSmokeClient shareLinkClient = new ShellShareLinkSmokeClient(shareLinkResult);
            ShellShareLinkSmokeClipboardService clipboard = new ShellShareLinkSmokeClipboardService();
            ShellShareLinkSmokeNotificationService notifications = new ShellShareLinkSmokeNotificationService();

            int exitCode = await RunShellShareLinkCopyAsync(
                paths,
                options,
                caseOutput,
                shareLinkClient: shareLinkClient,
                clipboardService: clipboard,
                notificationService: notifications,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string report = caseOutput.ToString();
            bool copiedMatches = ShellShareLinkCopyOutcomeMatches(
                expectCopied,
                exitCode,
                clipboard.CopiedText,
                shareLinkResult.ShareLink);
            bool failureMatches = ShellShareLinkFailureMatches(report, expectedFailureReason);
            bool notificationMatches = ShellShareLinkNotificationMatches(expectCopied, notifications.LastMessage);
            bool noPathLeak = ShellShareLinkReportHasNoPathLeak(report, selectedPath);
            bool resultMatches = report.Contains(expectCopied ? "Result: passed" : "Result: failed", StringComparison.Ordinal);
            bool passed = copiedMatches
                && failureMatches
                && notificationMatches
                && noPathLeak
                && resultMatches;

            return await WriteCheckAsync(
                output,
                passed,
                label,
                "exitCode=" + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", copied=" + FormatBoolean(clipboard.CopiedText is not null)
                    + ", notification=" + FormatBoolean(!string.IsNullOrWhiteSpace(notifications.LastMessage)))
                .ConfigureAwait(false);
        }

        private static bool ShellShareLinkCopyOutcomeMatches(
            bool expectCopied,
            int exitCode,
            string? copiedText,
            string? shareLink)
        {
            return expectCopied
                ? exitCode == 0 && copiedText == shareLink
                : exitCode != 0 && copiedText is null;
        }

        private static bool ShellShareLinkFailureMatches(string report, string? expectedFailureReason)
        {
            return expectedFailureReason is null
                ? !report.Contains("FailureReason:", StringComparison.Ordinal)
                : report.Contains("FailureReason: " + expectedFailureReason, StringComparison.Ordinal);
        }

        private static bool ShellShareLinkNotificationMatches(bool expectCopied, string? notification)
        {
            return expectCopied
                ? notification == "Share link copied to clipboard."
                : !string.IsNullOrWhiteSpace(notification);
        }

        private static bool ShellShareLinkReportHasNoPathLeak(string report, string selectedPath)
        {
            return !report.Contains(selectedPath, StringComparison.OrdinalIgnoreCase)
                && !report.Contains(Path.GetFileName(selectedPath), StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<DesktopShellShareLinkResult> CreateShellShareLinkAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            ShellShareLinkTarget target,
            IDesktopShellShareLinkClient? shareLinkClient,
            CancellationToken cancellationToken)
        {
            if (shareLinkClient is not null)
            {
                return await shareLinkClient.CreateShareLinkAsync(target, cancellationToken).ConfigureAwait(false);
            }

            Uri? serverUrl = await ResolveShellShareLinkServerUrlAsync(paths, startupOptions, cancellationToken)
                .ConfigureAwait(false);
            if (serverUrl is null)
            {
                return DesktopShellShareLinkResult.Unavailable("server-url-missing");
            }

            if (startupOptions.ServerUrl is not null
                && !await CanUseStoredSessionForServerAsync(paths, serverUrl, cancellationToken).ConfigureAwait(false))
            {
                return DesktopShellShareLinkResult.Failed("server-url-session-mismatch");
            }

            using HttpClient httpClient = DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30));
            FileCottonTokenStore tokenStore = new(paths.TokenStorePath);
            CottonSdkOptions sdkOptions = new()
            {
                BaseAddress = serverUrl,
                UserAgent = DesktopDeviceIdentity.CreateUserAgent(),
                DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
            };
            await using CottonCloudClient cottonClient = new(
                httpClient,
                tokenStore,
                sdkOptions,
                new DesktopTraceLoggerFactory());
            DesktopShellShareLinkClient client = new(
                httpClient,
                tokenStore,
                cottonClient.Auth,
                serverUrl);
            return await client.CreateShareLinkAsync(target, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Uri?> ResolveShellShareLinkServerUrlAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            CancellationToken cancellationToken)
        {
            if (startupOptions.ServerUrl is not null)
            {
                return startupOptions.ServerUrl;
            }

            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            return preferences.RememberedServerUrl;
        }

        private static async Task<bool> CanUseStoredSessionForServerAsync(
            DesktopAppPaths paths,
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            SqliteAppPreferencesStore preferencesStore = new(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            return preferences.RememberedServerUrl is not null
                && preferences.RememberedServerUrl.Equals(serverUrl);
        }
    }
}
