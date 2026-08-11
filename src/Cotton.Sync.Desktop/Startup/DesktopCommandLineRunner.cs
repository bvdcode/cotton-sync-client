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
    internal static class DesktopCommandLineRunner
    {
        private const int FinalConvergencePasses = 3;
        private const string LocalUploadPath = "local-upload.txt";
        private const string LocalRenamedPath = "local-renamed.txt";
        private const string RemoteOriginPath = "remote-origin.txt";
        private const string RemoteRenamedPath = "remote-renamed.txt";
        private const string PreExistingClientAPath = "pre-existing/client-a/original-a.txt";
        private const string PreExistingClientBPath = "pre-existing/client-b/original-b.txt";
        private static readonly TimeSpan DesktopLocalQuietWindow = TimeSpan.FromMilliseconds(2300);
        private static readonly TimeSpan InitialConvergenceTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PropagationTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PropagationPollInterval = TimeSpan.FromSeconds(1);
        private const int InitialConvergenceSyncRefreshInterval = 10;

        public static async Task<int> RunSelfTestAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunSelfTestAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<int> RunSelfTestAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            DesktopTraceLogging.Install(paths);
            await using DesktopShellController controller = DesktopShellController.CreateDefault(paths, startupOptions);
            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("Cotton Sync Desktop self-test").ConfigureAwait(false);
            foreach (DesktopSelfTestItemSnapshot item in result.Items)
            {
                await output.WriteLineAsync(FormatSelfTestItem(item)).ConfigureAwait(false);
            }

            await output.WriteLineAsync(result.Passed ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return result.Passed ? 0 : 1;
        }

        public static async Task<int> RunExportDiagnosticsAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunExportDiagnosticsAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<int> RunExportDiagnosticsAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            DesktopTraceLogging.Install(paths);
            await using DesktopShellController controller = DesktopShellController.CreateDefault(paths, startupOptions);
            DesktopDiagnosticsExportOptions exportOptions = startupOptions.ExportPrivateSupportDiagnostics
                ? DesktopDiagnosticsExportOptions.PrivateSupport
                : DesktopDiagnosticsExportOptions.Public;
            string bundlePath = await controller.ExportDiagnosticsAsync(exportOptions, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("Cotton Sync Desktop diagnostics").ConfigureAwait(false);
            await output.WriteLineAsync("Mode: " + exportOptions.DisplayName).ConfigureAwait(false);
            await output.WriteLineAsync("Bundle: " + bundlePath).ConfigureAwait(false);
            return 0;
        }

        internal static async Task<int> RunCloudFilesCleanupAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            IWindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            DesktopTraceLogging.Install(paths);
            var syncPairs = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var syncState = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await syncState.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncPairSettings> configuredPairs = await syncPairs
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
            IWindowsCloudFilesAdapter cloudFiles = cloudFilesAdapter ?? new WindowsCloudFilesAdapter();
            await output.WriteLineAsync("Cotton Sync Desktop Cloud Files cleanup").ConfigureAwait(false);
            (int cleaned, int failures) = await CleanupConfiguredCloudFilesPairsAsync(
                configuredPairs,
                syncState,
                cloudFiles,
                output,
                cancellationToken).ConfigureAwait(false);
            IWindowsStorageProviderSyncRootRegistrar? registrar =
                storageProviderRegistrar ?? WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            failures += await CleanupOrphanedStorageProviderRootsAsync(registrar, output).ConfigureAwait(false);
            return await WriteCloudFilesCleanupResultAsync(output, cleaned, failures).ConfigureAwait(false);
        }

        private static async Task<(int Cleaned, int Failures)> CleanupConfiguredCloudFilesPairsAsync(
            IEnumerable<SyncPairSettings> configuredPairs,
            ISyncStateStore syncState,
            IWindowsCloudFilesAdapter cloudFiles,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int cleaned = 0;
            int failures = 0;
            foreach (SyncPairSettings syncPair in configuredPairs.Where(static pair => pair.Mode == SyncPairMode.WindowsVirtualFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await CleanupConfiguredCloudFilesPairAsync(
                        syncPair,
                        syncState,
                        cloudFiles,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    cleaned++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    await output
                        .WriteLineAsync("Failed: " + syncPair.LocalRootPath + " - " + CleanSingleLine(exception.Message))
                        .ConfigureAwait(false);
                }
            }

            return (cleaned, failures);
        }

        private static async Task CleanupConfiguredCloudFilesPairAsync(
            SyncPairSettings syncPair,
            ISyncStateStore syncState,
            IWindowsCloudFilesAdapter cloudFiles,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            SyncChangeCursor cursor = await syncState
                .GetChangeCursorAsync(syncPair.Id.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            cursor.HasCompletedFullReconcile = false;
            cursor.UpdatedAtUtc = DateTime.UtcNow;
            await syncState.SaveChangeCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
            cloudFiles.UnregisterSyncRoot(syncPair);
            await output.WriteLineAsync("Unregistered: " + syncPair.LocalRootPath).ConfigureAwait(false);
            await output.WriteLineAsync("Recovery queued: " + syncPair.LocalRootPath).ConfigureAwait(false);
        }

        private static async Task<int> CleanupOrphanedStorageProviderRootsAsync(
            IWindowsStorageProviderSyncRootRegistrar? registrar,
            TextWriter output)
        {
            if (registrar is null)
            {
                return 0;
            }

            try
            {
                if (!registrar.IsSupported())
                {
                    await output
                        .WriteLineAsync("Orphaned storage-provider cleanup skipped: Windows StorageProvider is unavailable.")
                        .ConfigureAwait(false);
                    return 0;
                }

                registrar.UnregisterAllForCurrentUser();
                await output.WriteLineAsync("Orphaned storage-provider roots cleaned.").ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output
                    .WriteLineAsync("Failed orphaned storage-provider cleanup: " + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<int> WriteCloudFilesCleanupResultAsync(
            TextWriter output,
            int cleaned,
            int failures)
        {
            await output.WriteLineAsync("Roots cleaned: " + cleaned.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        internal static async Task<int> RunShellShareLinkTargetAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IShellShareLinkTargetResolver? resolver = null,
            IDesktopShellShareLinkClient? shareLinkClient = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);
            if (string.IsNullOrWhiteSpace(startupOptions.ShellShareLinkTargetPath))
            {
                await output.WriteLineAsync("--resolve-shell-share-link-target requires a local file or folder path.")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            DesktopTraceLogging.Install(paths);
            (ShellShareLinkTarget target, DesktopShellShareLinkResult shareLinkResult) =
                await ResolveShellShareLinkAsync(
                    paths,
                    startupOptions,
                    startupOptions.ShellShareLinkTargetPath,
                    resolver,
                    shareLinkClient,
                    cancellationToken).ConfigureAwait(false);
            bool targetResolved = target.Status == ShellShareLinkTargetStatus.Resolved;
            bool canCreateShareLink = target.CanCreateShareLink && shareLinkResult.IsCreated;
            await WriteShellShareLinkTargetReportAsync(
                output,
                target,
                shareLinkResult,
                targetResolved,
                canCreateShareLink).ConfigureAwait(false);
            return canCreateShareLink ? 0 : 1;
        }

        private static async Task WriteShellShareLinkTargetReportAsync(
            TextWriter output,
            ShellShareLinkTarget target,
            DesktopShellShareLinkResult shareLinkResult,
            bool targetResolved,
            bool canCreateShareLink)
        {
            await output.WriteLineAsync("Cotton Sync Desktop shell share-link target").ConfigureAwait(false);
            await output.WriteLineAsync("Status: " + FormatShellShareLinkTargetStatus(target.Status))
                .ConfigureAwait(false);
            await output.WriteLineAsync("TargetResolved: " + FormatBoolean(targetResolved))
                .ConfigureAwait(false);
            await output.WriteLineAsync("TargetHasRemoteIdentity: " + FormatBoolean(target.CanCreateShareLink))
                .ConfigureAwait(false);
            await output.WriteLineAsync("ShareLinkApi: " + (shareLinkResult.IsApiAvailable ? "available" : "unavailable"))
                .ConfigureAwait(false);
            await output.WriteLineAsync("CanCreateShareLink: " + FormatBoolean(canCreateShareLink))
                .ConfigureAwait(false);
            await output.WriteLineAsync("ShareLinkCreated: " + FormatBoolean(shareLinkResult.IsCreated))
                .ConfigureAwait(false);
            if (shareLinkResult.IsCreated && !string.IsNullOrWhiteSpace(shareLinkResult.ShareLink))
            {
                await output.WriteLineAsync("ShareLink: " + CleanSingleLine(shareLinkResult.ShareLink))
                    .ConfigureAwait(false);
            }

            if (targetResolved && !canCreateShareLink && !string.IsNullOrWhiteSpace(shareLinkResult.FailureReason))
            {
                await output.WriteLineAsync("FailureReason: " + shareLinkResult.FailureReason)
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync("TargetKind: " + FormatShellShareLinkTargetKind(target.Kind))
                .ConfigureAwait(false);
            await output.WriteLineAsync("HasSyncPair: " + FormatBoolean(target.SyncPairId.HasValue))
                .ConfigureAwait(false);
            await output.WriteLineAsync("HasRemoteNodeId: " + FormatBoolean(target.RemoteNodeId.HasValue))
                .ConfigureAwait(false);
            await output.WriteLineAsync("HasRemoteFileId: " + FormatBoolean(target.RemoteFileId.HasValue))
                .ConfigureAwait(false);
            await output.WriteLineAsync(canCreateShareLink ? "Result: passed" : "Result: failed")
                .ConfigureAwait(false);
        }

        internal static async Task<int> RunShellShareLinkCopyAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IShellShareLinkTargetResolver? resolver = null,
            IDesktopShellShareLinkClient? shareLinkClient = null,
            IDesktopClipboardService? clipboardService = null,
            IDesktopNotificationService? notificationService = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);
            if (string.IsNullOrWhiteSpace(startupOptions.ShellCopyShareLinkTargetPath))
            {
                await output.WriteLineAsync("--copy-shell-share-link requires a local file or folder path.")
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            DesktopTraceLogging.Install(paths);
            (ShellShareLinkTarget target, DesktopShellShareLinkResult shareLinkResult) =
                await ResolveShellShareLinkAsync(
                    paths,
                    startupOptions,
                    startupOptions.ShellCopyShareLinkTargetPath,
                    resolver,
                    shareLinkClient,
                    cancellationToken).ConfigureAwait(false);
            (bool copied, string? failureReason) = await TryCopyShellShareLinkAsync(
                    target,
                    shareLinkResult,
                    clipboardService,
                    cancellationToken)
                .ConfigureAwait(false);
            IDesktopNotificationService effectiveNotificationService =
                notificationService ?? DesktopNotificationServiceFactory.CreateDefault();
            ShowShellShareLinkCopyNotification(effectiveNotificationService, copied, failureReason);
            await WriteShellShareLinkCopyReportAsync(
                output,
                target,
                shareLinkResult,
                copied,
                failureReason).ConfigureAwait(false);
            return copied ? 0 : 1;
        }

        private static async Task<(bool Copied, string? FailureReason)> TryCopyShellShareLinkAsync(
            ShellShareLinkTarget target,
            DesktopShellShareLinkResult shareLinkResult,
            IDesktopClipboardService? clipboardService,
            CancellationToken cancellationToken)
        {
            if (!target.CanCreateShareLink)
            {
                return (false, "target-" + FormatShellShareLinkTargetStatus(target.Status));
            }

            if (!shareLinkResult.IsCreated || string.IsNullOrWhiteSpace(shareLinkResult.ShareLink))
            {
                string failureReason = string.IsNullOrWhiteSpace(shareLinkResult.FailureReason)
                    ? "share-link-unavailable"
                    : shareLinkResult.FailureReason;
                return (false, failureReason);
            }

            IDesktopClipboardService effectiveClipboardService =
                clipboardService ?? DesktopClipboardServiceFactory.CreateDefault();
            try
            {
                await effectiveClipboardService.CopyTextAsync(shareLinkResult.ShareLink, cancellationToken)
                    .ConfigureAwait(false);
                return (true, null);
            }
            catch (Exception exception) when (IsExpectedClipboardFailure(exception))
            {
                Trace.TraceWarning("Failed to copy shell share link to clipboard: {0}", exception);
                return (false, "clipboard-unavailable");
            }
        }

        private static async Task WriteShellShareLinkCopyReportAsync(
            TextWriter output,
            ShellShareLinkTarget target,
            DesktopShellShareLinkResult shareLinkResult,
            bool copied,
            string? failureReason)
        {
            await output.WriteLineAsync("Cotton Sync Desktop copy share link").ConfigureAwait(false);
            await output.WriteLineAsync("Status: " + FormatShellShareLinkTargetStatus(target.Status))
                .ConfigureAwait(false);
            await output.WriteLineAsync("ShareLinkApi: " + (shareLinkResult.IsApiAvailable ? "available" : "unavailable"))
                .ConfigureAwait(false);
            await output.WriteLineAsync("ShareLinkCreated: " + FormatBoolean(shareLinkResult.IsCreated))
                .ConfigureAwait(false);
            await output.WriteLineAsync("ShareLinkCopied: " + FormatBoolean(copied))
                .ConfigureAwait(false);
            if (!copied && !string.IsNullOrWhiteSpace(failureReason))
            {
                await output.WriteLineAsync("FailureReason: " + CleanSingleLine(failureReason))
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync(copied ? "Result: passed" : "Result: failed").ConfigureAwait(false);
        }

        internal static async Task<int> RunSocketCleanupSmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            DesktopTraceLogging.Install(paths);
            SocketCleanupSmokeTraceListener listener = new();
            Trace.Listeners.Add(listener);
            int observedEvents = 0;

            try
            {
                for (int index = 0; index < 3; index++)
                {
                    AggregateException exception =
                        new(new SocketException((int)SocketError.OperationAborted));
                    UnobservedTaskExceptionEventArgs args = new(exception);
                    DesktopUnhandledExceptionReporter.ReportUnobservedTaskException(args);
                    if (args.Observed)
                    {
                        observedEvents++;
                    }
                }
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }

            string capturedTrace = listener.Output;
            bool unexpectedLogWritten = capturedTrace.Contains(
                "Unobserved desktop task exception captured.",
                StringComparison.Ordinal);
            bool passed = observedEvents == 3 && !unexpectedLogWritten;

            await output.WriteLineAsync("Cotton Sync Desktop socket cleanup smoke").ConfigureAwait(false);
            await output.WriteLineAsync(
                "ExpectedCleanupEventsObserved: "
                + observedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/3").ConfigureAwait(false);
            await output.WriteLineAsync("UnexpectedUnobservedSocketCleanupLog: " + FormatBoolean(unexpectedLogWritten))
                .ConfigureAwait(false);
            await output.WriteLineAsync(passed ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return passed ? 0 : 1;
        }

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
                Id = Guid.CreateVersion7(),
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

            using HttpClient httpClient = DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30));
            var tokenStore = new FileCottonTokenStore(paths.TokenStorePath);
            var sdkOptions = new CottonSdkOptions
            {
                BaseAddress = serverUrl,
                UserAgent = DesktopDeviceIdentity.CreateUserAgent(),
                DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
            };
            await using var cottonClient = new CottonCloudClient(
                httpClient,
                tokenStore,
                sdkOptions,
                new DesktopTraceLoggerFactory());
            var client = new DesktopShellShareLinkClient(
                httpClient,
                tokenStore,
                cottonClient.Auth,
                serverUrl);
            return await client.CreateShareLinkAsync(target, cancellationToken).ConfigureAwait(false);
        }

        private static void ShowShellShareLinkCopyNotification(
            IDesktopNotificationService notificationService,
            bool copied,
            string? failureReason)
        {
            string message = copied
                ? "Share link copied to clipboard."
                : FormatShellShareLinkFailureMessage(failureReason);
            notificationService.Show("Cotton Sync", message);
        }

        private static string FormatShellShareLinkFailureMessage(string? failureReason)
        {
            return failureReason switch
            {
                "target-missing-baseline" => "This item is not synced yet.",
                "target-missing-remote-identity" => "This item is not ready for sharing yet.",
                "target-ignored-path" => "This item is not available for sharing.",
                "target-outside-sync-root" => "Select an item inside a synced folder.",
                "target-sync-pair-disabled" => "Enable this synced folder and try again.",
                "server-url-missing"
                    or "token-missing"
                    or "refresh-failed"
                    or "auth-token-missing"
                    or "auth-refresh-failed" => "Sign in to Cotton Sync and try again.",
                "clipboard-unavailable" => "The share link was created, but the clipboard is unavailable.",
                _ => "Share link could not be copied.",
            };
        }

        private static bool IsExpectedClipboardFailure(Exception exception)
        {
            return exception is IOException
                or InvalidOperationException
                or NotSupportedException
                or ObjectDisposedException
                or OperationCanceledException;
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

            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            return preferences.RememberedServerUrl;
        }

        public static async Task<int> RunLiveSyncSmokeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunLiveSyncSmokeAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<int> RunWindowsVirtualFilesSmokeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunWindowsVirtualFilesSmokeAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public static async Task<int> RunUpdateDiscoverySmokeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunUpdateDiscoverySmokeAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public static async Task<int> RunUpdateInstallSmokeAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            return await RunUpdateInstallSmokeAsync(
                DesktopStartupPathResolver.Resolve(startupOptions),
                startupOptions,
                output,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<int> RunUpdateDiscoverySmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IDesktopUpdateService? updateService = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            string? validationError = ValidateUpdateDiscoverySmokeOptions(startupOptions);
            if (validationError is not null)
            {
                await output.WriteLineAsync("Cotton Sync Desktop update discovery smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + validationError).ConfigureAwait(false);
                return 2;
            }

            Directory.CreateDirectory(paths.DataDirectory);
            DesktopTraceLogging.Install(paths);
            (IDesktopUpdateService effectiveUpdateService, IDisposable? updateServiceLifetime) =
                CreateUpdateDiscoveryService(paths, startupOptions, updateService);

            try
            {
                return await RunUpdateDiscoveryWorkflowAsync(
                        paths,
                        startupOptions,
                        effectiveUpdateService,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await output.WriteLineAsync("Cotton Sync Desktop update discovery smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + exception.GetType().Name + ": " + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
            finally
            {
                updateServiceLifetime?.Dispose();
            }
        }

        private static (IDesktopUpdateService Service, IDisposable? Lifetime) CreateUpdateDiscoveryService(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IDesktopUpdateService? configuredService)
        {
            if (configuredService is not null)
            {
                return (configuredService, null);
            }

            DesktopUpdateService service = new(
                DesktopHttpClientFactory.Create(TimeSpan.FromSeconds(30)),
                DesktopAppVersion.Current,
                paths.UpdateCacheDirectory,
                startupOptions.UpdateManifestUri,
                DesktopUpdatePlatform.WindowsX64,
                DesktopUpdateSourceTrustPolicy.CreateForSmokeManifest(startupOptions.UpdateManifestUri!),
                disposeHttpClient: true);
            return (service, service);
        }

        private static async Task<int> RunUpdateDiscoveryWorkflowAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IDesktopUpdateService updateService,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await using DesktopShellController controller = CreateUpdateSmokeController(
                paths,
                startupOptions,
                updateService);
            await output.WriteLineAsync("Cotton Sync Desktop update discovery smoke").ConfigureAwait(false);
            await output.WriteLineAsync("Current version: " + DesktopAppVersion.Current).ConfigureAwait(false);
            await output.WriteLineAsync("Manifest: " + startupOptions.UpdateManifestUri).ConfigureAwait(false);

            DesktopUpdateStatusSnapshot status = await controller
                .DownloadUpdateAsync(DesktopUpdateCheckSource.Download, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            DesktopPendingUpdate? pendingUpdate = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory).TryLoad();
            int failures = await VerifyUpdateDiscoveryAsync(
                    paths,
                    startupOptions,
                    controller,
                    status,
                    pendingUpdate,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync("Latest version: " + (status.LatestVersion ?? "<none>")).ConfigureAwait(false);
            await output.WriteLineAsync("Installer ready: " + (status.IsInstallerReady ? "yes" : "no")).ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task<int> VerifyUpdateDiscoveryAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            DesktopShellController controller,
            DesktopUpdateStatusSnapshot status,
            DesktopPendingUpdate? pendingUpdate,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            failures += await WriteCheckAsync(
                output,
                status.IsUpdateAvailable,
                "Installed version discovers a newer release",
                "current=" + status.CurrentVersion + ", latest=" + (status.LatestVersion ?? "<none>")).ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                ExpectedUpdateVersionMatches(startupOptions.ExpectedUpdateVersion, status.LatestVersion),
                "Latest version matches expected release",
                "expected=" + (startupOptions.ExpectedUpdateVersion ?? "<not-set>")
                    + ", latest=" + (status.LatestVersion ?? "<none>")).ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                IsUpdateInstallerReady(status),
                "Update installer is downloaded into cache",
                "installerReady=" + status.IsInstallerReady).ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                IsPendingUpdateReady(pendingUpdate, status.LatestVersion),
                "Pending update metadata is persisted",
                "pendingVersion=" + (pendingUpdate?.Version ?? "<none>")).ConfigureAwait(false);
            string diagnosticsBundlePath = await controller
                .ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                File.Exists(diagnosticsBundlePath),
                "Diagnostics bundle records update status",
                "bundle=" + diagnosticsBundlePath).ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                File.Exists(paths.LogFilePath),
                "Update flow wrote a trace log",
                "log=" + paths.LogFilePath).ConfigureAwait(false);
            await output.WriteLineAsync("Bundle: " + diagnosticsBundlePath).ConfigureAwait(false);
            return failures;
        }

        private static bool ExpectedUpdateVersionMatches(string? expectedVersion, string? latestVersion)
        {
            return string.IsNullOrWhiteSpace(expectedVersion)
                || string.Equals(latestVersion, expectedVersion, StringComparison.Ordinal);
        }

        private static bool IsUpdateInstallerReady(DesktopUpdateStatusSnapshot status)
        {
            return status.IsInstallerReady
                && !string.IsNullOrWhiteSpace(status.InstallerPath)
                && File.Exists(status.InstallerPath);
        }

        private static bool IsPendingUpdateReady(DesktopPendingUpdate? pendingUpdate, string? latestVersion)
        {
            return pendingUpdate is not null
                && string.Equals(pendingUpdate.Version, latestVersion, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(pendingUpdate.InstallerPath)
                && File.Exists(pendingUpdate.InstallerPath)
                && pendingUpdate.SizeBytes > 0;
        }

        internal static async Task<int> RunUpdateInstallSmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IDesktopUpdateInstaller? updateInstaller = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            string? validationError = ValidateUpdateInstallSmokeOptions(startupOptions);
            if (validationError is not null)
            {
                await output.WriteLineAsync("Cotton Sync Desktop update install smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + validationError).ConfigureAwait(false);
                return 2;
            }

            Directory.CreateDirectory(paths.DataDirectory);
            DesktopTraceLogging.Install(paths);
            try
            {
                IDesktopUpdateInstaller effectiveInstaller = updateInstaller ?? CreateUpdateInstallSmokeInstaller();
                return await RunUpdateInstallWorkflowAsync(
                        paths,
                        startupOptions,
                        effectiveInstaller,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await output.WriteLineAsync("Cotton Sync Desktop update install smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + exception.GetType().Name + ": " + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<int> RunUpdateInstallWorkflowAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IDesktopUpdateInstaller updateInstaller,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await using DesktopShellController controller = CreateUpdateSmokeController(
                paths,
                startupOptions,
                updateInstaller: updateInstaller);
            await output.WriteLineAsync("Cotton Sync Desktop update install smoke").ConfigureAwait(false);
            DesktopUpdateInstallResult result = await controller
                .InstallDownloadedUpdateAsync(startupOptions.UpdateInstallerPath!, cancellationToken)
                .ConfigureAwait(false);
            string diagnosticsBundlePath = await controller
                .ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
            int failures = await VerifyUpdateInstallAsync(
                    paths,
                    result,
                    diagnosticsBundlePath,
                    output)
                .ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task<int> VerifyUpdateInstallAsync(
            DesktopAppPaths paths,
            DesktopUpdateInstallResult result,
            string diagnosticsBundlePath,
            TextWriter output)
        {
            int failures = 0;
            failures += await WriteCheckAsync(
                output,
                result.ProcessId > 0,
                "Update installer launch returns a process id",
                "processId=" + result.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                InstallerStartupProbePassed(result),
                "Update installer startup probe does not fail",
                "exitedDuringProbe=" + result.ExitedDuringStartupProbe
                    + ", exitCode=" + (result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<running>"))
                .ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                File.Exists(diagnosticsBundlePath),
                "Diagnostics bundle records installer launch outcome",
                "bundle=" + diagnosticsBundlePath).ConfigureAwait(false);
            failures += await WriteCheckAsync(
                output,
                File.Exists(paths.LogFilePath),
                "Installer launch wrote a trace log",
                "log=" + paths.LogFilePath).ConfigureAwait(false);
            return failures;
        }

        private static bool InstallerStartupProbePassed(DesktopUpdateInstallResult result)
        {
            return !result.ExitedDuringStartupProbe || result.ExitCode.GetValueOrDefault() == 0;
        }

        internal static async Task<int> RunWindowsVirtualFilesSmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            Func<string, CancellationToken, Task<string>>? readAllTextAsync = null,
            CancellationToken cancellationToken = default)
        {
            return await DesktopWindowsVirtualFilesSmokeRunner
                .RunAsync(
                    paths,
                    startupOptions,
                    output,
                    cloudFilesAdapter,
                    readAllTextAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        internal static async Task<int> RunLiveSyncSmokeAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            string? validationError = ValidateLiveSyncSmokeOptions(paths, startupOptions);
            if (validationError is not null)
            {
                await output.WriteLineAsync("Cotton Sync Desktop live sync smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + validationError).ConfigureAwait(false);
                return 2;
            }

            Directory.CreateDirectory(paths.DataDirectory);
            Directory.CreateDirectory(startupOptions.LocalRoot!);
            Directory.CreateDirectory(startupOptions.SecondLocalRoot!);
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles = await PrepareLiveSmokeSeedFilesAsync(
                    startupOptions,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            DesktopTraceLogging.Install(paths);

            DesktopAppPaths firstPaths = DesktopAppPaths.CreateForDataDirectory(
                Path.Combine(paths.DataDirectory, "client-a-state"));
            DesktopAppPaths secondPaths = DesktopAppPaths.CreateForDataDirectory(
                Path.Combine(paths.DataDirectory, "client-b-state"));
            await using DesktopShellController firstController = CreateLiveSmokeController(
                firstPaths,
                startupOptions,
                output);
            await using DesktopShellController secondController = CreateLiveSmokeController(
                secondPaths,
                startupOptions,
                output);

            DesktopLiveSyncSmokeSession session = new(
                firstPaths,
                secondPaths,
                firstController,
                secondController);
            try
            {
                return await RunLiveSyncWorkflowAsync(
                        paths,
                        startupOptions,
                        seededLocalFiles,
                        session,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                await output.WriteLineAsync("Error: " + exception.GetType().Name + ": " + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
            finally
            {
                await CleanupLiveSyncSmokeAsync(session, output).ConfigureAwait(false);
            }
        }

        private static async Task<IReadOnlyList<LiveSyncSmokeSeededLocalFile>> PrepareLiveSmokeSeedFilesAsync(
            DesktopStartupOptions startupOptions,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (!startupOptions.LiveSyncSmokePreserveExistingLocalFiles)
            {
                return [];
            }

            return await SeedExistingLocalFilesAsync(startupOptions, output, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> RunLiveSyncWorkflowAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await WriteLiveSyncSmokeHeaderAsync(paths, startupOptions, output).ConfigureAwait(false);
            await SignInLiveSmokeClientsAsync(startupOptions, session, output, cancellationToken).ConfigureAwait(false);
            await AddLiveSmokeSyncPairsAsync(startupOptions, session, cancellationToken).ConfigureAwait(false);

            int failures = await VerifyInitialLiveSyncStateAsync(
                    startupOptions,
                    seededLocalFiles,
                    session,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            failures += await RunLiveSyncMutationSequenceAsync(
                    startupOptions,
                    session,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            failures += await VerifyFinalLiveSyncStateAsync(
                    seededLocalFiles,
                    session,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync("Converged: " + (failures == 0 ? "yes" : "no")).ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static async Task WriteLiveSyncSmokeHeaderAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output)
        {
            await output.WriteLineAsync("Cotton Sync Desktop live sync smoke").ConfigureAwait(false);
            await output.WriteLineAsync("Server: " + startupOptions.ServerUrl).ConfigureAwait(false);
            await output.WriteLineAsync("Remote root: " + startupOptions.RemotePath).ConfigureAwait(false);
            await output.WriteLineAsync("Local root: " + startupOptions.LocalRoot).ConfigureAwait(false);
            await output.WriteLineAsync("Second local root: " + startupOptions.SecondLocalRoot).ConfigureAwait(false);
            await output.WriteLineAsync("Sync mode: " + startupOptions.SyncMode).ConfigureAwait(false);
            await output.WriteLineAsync("Data root: " + paths.DataDirectory).ConfigureAwait(false);
        }

        private static async Task SignInLiveSmokeClientsAsync(
            DesktopStartupOptions startupOptions,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await output.WriteLineAsync("Approving first desktop client...").ConfigureAwait(false);
            await session.FirstController.SignInWithBrowserAsync(
                startupOptions.ServerUrl!.AbsoluteUri,
                cancellationToken).ConfigureAwait(false);
            session.FirstSignedIn = true;
            await output.WriteLineAsync("Approving second desktop client...").ConfigureAwait(false);
            await session.SecondController.SignInWithBrowserAsync(
                startupOptions.ServerUrl.AbsoluteUri,
                cancellationToken).ConfigureAwait(false);
            session.SecondSignedIn = true;
        }

        private static async Task AddLiveSmokeSyncPairsAsync(
            DesktopStartupOptions startupOptions,
            DesktopLiveSyncSmokeSession session,
            CancellationToken cancellationToken)
        {
            session.FirstPair = await session.FirstController.AddSyncPairAsync(
                new DesktopSyncPairRequest(startupOptions.LocalRoot!, startupOptions.RemotePath!, startupOptions.SyncMode),
                cancellationToken).ConfigureAwait(false);
            session.SecondPair = await session.SecondController.AddSyncPairAsync(
                new DesktopSyncPairRequest(startupOptions.SecondLocalRoot!, startupOptions.RemotePath!, startupOptions.SyncMode),
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> VerifyInitialLiveSyncStateAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Guid firstPairId = session.FirstPair!.Id;
            Guid secondPairId = session.SecondPair!.Id;
            int failures = await WaitForLiveSmokeConvergenceAsync(
                startupOptions,
                seededLocalFiles,
                session.FirstController,
                session.SecondController,
                firstPairId,
                secondPairId,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifyIdleAsync(
                session.FirstController,
                session.SecondController,
                firstPairId,
                secondPairId,
                "Initial desktop sync reached idle/up-to-date.",
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifySeededLocalFilesAsync(
                seededLocalFiles,
                "Pre-existing local files survived sync pair creation.",
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await VerifyLiveSyncDiagnosticsAsync(
                session.FirstController,
                firstPairId,
                output,
                cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> VerifyLiveSyncDiagnosticsAsync(
            DesktopShellController controller,
            Guid syncPairId,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string diagnosticsBundlePath = await controller
                .ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken)
                .ConfigureAwait(false);
            LiveSyncSmokeDiagnosticsVerification verification =
                LiveSyncSmokeDiagnosticsVerifier.Verify(diagnosticsBundlePath, syncPairId);
            await output.WriteLineAsync(FormatCheck(
                verification.Passed,
                "Connected public diagnostics bundle is complete and sanitized. " + verification.Details))
                .ConfigureAwait(false);
            return verification.Passed ? 0 : 1;
        }

        private static async Task<int> RunLiveSyncMutationSequenceAsync(
            DesktopStartupOptions startupOptions,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            failures += await RunClientACreateAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBCreateAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientARenameAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBRenameAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientADeleteAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            failures += await RunClientBDeleteAsync(
                startupOptions,
                session.FirstController,
                session.SecondController,
                output,
                cancellationToken).ConfigureAwait(false);
            return failures;
        }

        private static async Task<int> VerifyFinalLiveSyncStateAsync(
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopLiveSyncSmokeSession session,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            await RunFinalConvergenceAsync(
                session.FirstController,
                session.SecondController,
                cancellationToken).ConfigureAwait(false);
            int failures = await VerifySeededLocalFilesAsync(
                seededLocalFiles,
                "Pre-existing local files survived final convergence.",
                output,
                cancellationToken).ConfigureAwait(false);
            int finalStateEntries = await CountStateEntriesAsync(
                    session.FirstPaths,
                    session.FirstPair!.Id,
                    cancellationToken)
                .ConfigureAwait(false)
                + await CountStateEntriesAsync(
                        session.SecondPaths,
                        session.SecondPair!.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            IReadOnlyList<string> expectedStatePaths = LiveSyncSmokeStateExpectation.BuildRelativePaths(
                seededLocalFiles.Select(static file => file.RelativePath));
            int expectedFinalStateEntries = expectedStatePaths.Count * 2;
            await output.WriteLineAsync("Final state entries: " + finalStateEntries.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                "Expected final state entries: "
                + expectedFinalStateEntries.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return finalStateEntries == expectedFinalStateEntries ? failures : failures + 1;
        }

        private static async Task CleanupLiveSyncSmokeAsync(
            DesktopLiveSyncSmokeSession session,
            TextWriter output)
        {
            if (session.FirstPair is not null)
            {
                await TryRemoveLiveSmokeSyncPairAsync(
                    session.FirstController,
                    session.FirstPair,
                    output,
                    "first").ConfigureAwait(false);
            }

            if (session.SecondPair is not null)
            {
                await TryRemoveLiveSmokeSyncPairAsync(
                    session.SecondController,
                    session.SecondPair,
                    output,
                    "second").ConfigureAwait(false);
            }

            if (session.FirstSignedIn)
            {
                await TrySignOutAsync(session.FirstController, output, "first").ConfigureAwait(false);
            }

            if (session.SecondSignedIn)
            {
                await TrySignOutAsync(session.SecondController, output, "second").ConfigureAwait(false);
            }
        }

        private static string FormatSelfTestItem(DesktopSelfTestItemSnapshot item)
        {
            string status = item.Skipped ? "SKIP" : item.Passed ? "OK" : "FAIL";
            return "[" + status + "] " + item.Name + " - " + item.Details;
        }

        private static string FormatShellShareLinkTargetStatus(ShellShareLinkTargetStatus status)
        {
            return status switch
            {
                ShellShareLinkTargetStatus.Resolved => "resolved",
                ShellShareLinkTargetStatus.OutsideSyncRoot => "outside-sync-root",
                ShellShareLinkTargetStatus.SyncPairDisabled => "sync-pair-disabled",
                ShellShareLinkTargetStatus.IgnoredPath => "ignored-path",
                ShellShareLinkTargetStatus.MissingBaseline => "missing-baseline",
                ShellShareLinkTargetStatus.MissingRemoteIdentity => "missing-remote-identity",
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown shell share-link target status."),
            };
        }

        private static string FormatShellShareLinkTargetKind(ShellShareLinkTargetKind kind)
        {
            return kind switch
            {
                ShellShareLinkTargetKind.Unknown => "unknown",
                ShellShareLinkTargetKind.File => "file",
                ShellShareLinkTargetKind.Directory => "directory",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shell share-link target kind."),
            };
        }

        private static string FormatBoolean(bool value)
        {
            return value ? "true" : "false";
        }

        private static DesktopShellController CreateLiveSmokeController(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output)
        {
            var loggerFactory = new DesktopTraceLoggerFactory();
            var platformCommands = new LiveSmokePlatformCommandService(output, startupOptions.LiveSyncSmokeApprovalHold);
            return new DesktopShellController(
                paths,
                new DesktopSyncApplicationFactory(paths, loggerFactory, platformCommands),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                platformCommands,
                new UnsupportedAutostartService(),
                new DesktopShellControllerOptions
                {
                    StartupOptions = startupOptions,
                });
        }

        private static DesktopShellController CreateUpdateSmokeController(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IDesktopUpdateService? updateService = null,
            IDesktopUpdateInstaller? updateInstaller = null)
        {
            var loggerFactory = new DesktopTraceLoggerFactory();
            return new DesktopShellController(
                paths,
                new DesktopSyncApplicationFactory(paths, loggerFactory),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                new ProcessPlatformCommandService(
                    Microsoft.Extensions.Logging.LoggerFactoryExtensions
                        .CreateLogger<ProcessPlatformCommandService>(loggerFactory)),
                new UnsupportedAutostartService(),
                new DesktopShellControllerOptions
                {
                    StartupOptions = startupOptions,
                    UpdateService = updateService,
                    UpdateInstaller = updateInstaller,
                });
        }

        private static DesktopUpdateInstaller CreateUpdateInstallSmokeInstaller()
        {
            return new DesktopUpdateInstaller(new DesktopUpdateInstallerProcessLauncher(TimeSpan.FromSeconds(2)));
        }

        private static string? ValidateUpdateDiscoverySmokeOptions(DesktopStartupOptions startupOptions)
        {
            if (startupOptions.DataDirectory is null)
            {
                return "--update-discovery-smoke requires an explicit --data-dir so test state never uses the real user profile.";
            }

            if (startupOptions.UpdateManifestUri is null)
            {
                return "--update-discovery-smoke requires an absolute --update-manifest-url.";
            }

            if (startupOptions.UpdateManifestUri.Scheme != Uri.UriSchemeHttp
                && startupOptions.UpdateManifestUri.Scheme != Uri.UriSchemeHttps)
            {
                return "--update-manifest-url must use http or https.";
            }

            return null;
        }

        private static string? ValidateUpdateInstallSmokeOptions(DesktopStartupOptions startupOptions)
        {
            if (startupOptions.DataDirectory is null)
            {
                return "--update-install-smoke requires an explicit --data-dir so test state never uses the real user profile.";
            }

            if (string.IsNullOrWhiteSpace(startupOptions.UpdateInstallerPath))
            {
                return "--update-install-smoke requires an explicit --update-installer-path.";
            }

            if (!Path.IsPathFullyQualified(startupOptions.UpdateInstallerPath))
            {
                return "--update-installer-path must be an absolute path.";
            }

            if (!File.Exists(startupOptions.UpdateInstallerPath))
            {
                return "Update installer file does not exist.";
            }

            return null;
        }

        private static async Task<int> WriteCheckAsync(
            TextWriter output,
            bool passed,
            string label,
            string details)
        {
            await output.WriteLineAsync(FormatCheck(passed, label) + " " + details).ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static string? ValidateLiveSyncSmokeOptions(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions)
        {
            string? requiredOptionsError = ValidateRequiredLiveSyncSmokeOptions(startupOptions);
            return requiredOptionsError ?? ValidateLiveSyncSmokePaths(paths, startupOptions);
        }

        private static string? ValidateRequiredLiveSyncSmokeOptions(DesktopStartupOptions startupOptions)
        {
            if (startupOptions.SyncModeError is not null)
            {
                return startupOptions.SyncModeError;
            }

            if (startupOptions.ServerUrl is null)
            {
                return "--live-sync-smoke requires --server or --server-url.";
            }

            if (startupOptions.DataDirectory is null)
            {
                return "--live-sync-smoke requires an explicit --data-dir so test state never uses the real user profile.";
            }

            if (string.IsNullOrWhiteSpace(startupOptions.RemotePath))
            {
                return "--live-sync-smoke requires --remote-path.";
            }

            if (string.IsNullOrWhiteSpace(startupOptions.LocalRoot)
                || string.IsNullOrWhiteSpace(startupOptions.SecondLocalRoot))
            {
                return "--live-sync-smoke requires --local-root and --second-local-root.";
            }

            return null;
        }

        private static string? ValidateLiveSyncSmokePaths(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions)
        {
            if (string.IsNullOrWhiteSpace(startupOptions.LocalRoot)
                || string.IsNullOrWhiteSpace(startupOptions.SecondLocalRoot))
            {
                return "--live-sync-smoke requires --local-root and --second-local-root.";
            }

            string firstLocalRoot = startupOptions.LocalRoot;
            string secondLocalRoot = startupOptions.SecondLocalRoot;
            if (startupOptions.LiveSyncSmokeSeedFileCount.HasValue
                && !startupOptions.LiveSyncSmokePreserveExistingLocalFiles)
            {
                return "--live-sync-smoke-seed-file-count requires --live-sync-smoke-preserve-existing-local-files.";
            }

            if (Directory.Exists(paths.DataDirectory) && DataDirectoryHasUnexpectedEntries(paths.DataDirectory))
            {
                return "--data-dir must be empty or contain only the current smoke log for --live-sync-smoke.";
            }

            if (IsSameOrNestedPath(firstLocalRoot, secondLocalRoot))
            {
                return "--local-root and --second-local-root must be different and non-nested.";
            }

            string? firstRootError = ValidateEmptyOrMissingDirectory(firstLocalRoot, "--local-root");
            return firstRootError ?? ValidateEmptyOrMissingDirectory(secondLocalRoot, "--second-local-root");
        }

        private static bool DataDirectoryHasUnexpectedEntries(string dataDirectory)
        {
            return Directory
                .EnumerateFileSystemEntries(dataDirectory)
                .Any(static path => !string.Equals(
                    Path.GetFileName(path),
                    "cotton-sync.log",
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string? ValidateEmptyOrMissingDirectory(string path, string optionName)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            return Directory.EnumerateFileSystemEntries(path).Any()
                ? optionName + " must be empty or missing because --live-sync-smoke creates, renames, and deletes files inside it."
                : null;
        }

        private static async Task<int> VerifyIdleAsync(
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DesktopShellSnapshot firstSnapshot = await firstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondSnapshot = await secondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncPairSnapshot? firstPair = firstSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == firstPairId);
            DesktopSyncPairSnapshot? secondPair = secondSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == secondPairId);
            bool passed = AreLiveSmokePairsIdle(firstPair, secondPair);
            await output.WriteLineAsync(
                FormatCheck(passed, label)
                + " firstStatus=" + (firstPair?.Status ?? "<missing>")
                + ", secondStatus=" + (secondPair?.Status ?? "<missing>")).ConfigureAwait(false);
            return passed ? 0 : 1;
        }

        private static bool AreLiveSmokePairsIdle(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair)
        {
            return IsIdleWithoutError(firstPair) && IsIdleWithoutError(secondPair);
        }

        private static bool IsIdleWithoutError(DesktopSyncPairSnapshot? pair)
        {
            return pair is not null
                && string.Equals(pair.Status, "Idle", StringComparison.Ordinal)
                && pair.LastError is null;
        }

        private static async Task<int> WaitForLiveSmokeConvergenceAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow + InitialConvergenceTimeout;
            int attempts = 0;
            int stableObservations = 0;
            LiveSyncSmokeConvergenceSnapshot snapshot;
            await firstController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            await secondController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            do
            {
                attempts++;
                if (attempts > 1 && attempts % InitialConvergenceSyncRefreshInterval == 0)
                {
                    await firstController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                    await secondController.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                }

                snapshot = await CaptureLiveSmokeConvergenceAsync(
                        startupOptions,
                        seededLocalFiles,
                        firstController,
                        secondController,
                        firstPairId,
                        secondPairId,
                        cancellationToken)
                    .ConfigureAwait(false);
                stableObservations = snapshot.Passed ? stableObservations + 1 : 0;
                if (stableObservations >= 2)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Initial desktop sync reached stable convergence.")
                        + " attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", " + snapshot.Details).ConfigureAwait(false);
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
                FormatCheck(false, "Initial desktop sync reached stable convergence.")
                + " attempts=" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", " + snapshot.Details).ConfigureAwait(false);
            return 1;
        }

        private static async Task<LiveSyncSmokeConvergenceSnapshot> CaptureLiveSmokeConvergenceAsync(
            DesktopStartupOptions startupOptions,
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> seededLocalFiles,
            DesktopShellController firstController,
            DesktopShellController secondController,
            Guid firstPairId,
            Guid secondPairId,
            CancellationToken cancellationToken)
        {
            string[] localRoots = [startupOptions.LocalRoot!, startupOptions.SecondLocalRoot!];
            IReadOnlyDictionary<string, string> expectedHashes = BuildExpectedLiveSmokeHashes(
                localRoots,
                seededLocalFiles);
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> hashReads =
                await LiveSyncSmokeFileHashReader.ReadAsync(expectedHashes.Keys, cancellationToken)
                    .ConfigureAwait(false);
            (int availableFiles, int hashMismatches, int readFailures) = EvaluateLiveSmokeHashes(
                expectedHashes,
                hashReads);
            DesktopShellSnapshot firstSnapshot = await firstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondSnapshot = await secondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncPairSnapshot? firstPair = firstSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == firstPairId);
            DesktopSyncPairSnapshot? secondPair = secondSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == secondPairId);
            int expectedFiles = seededLocalFiles.Count * localRoots.Length;
            return new LiveSyncSmokeConvergenceSnapshot(
                LiveSmokeConverged(
                    firstPair,
                    secondPair,
                    availableFiles,
                    expectedFiles,
                    hashMismatches,
                    readFailures),
                FormatLiveSmokeConvergenceDetails(
                    firstPair,
                    secondPair,
                    availableFiles,
                    expectedFiles,
                    hashMismatches,
                    readFailures));
        }

        private static bool LiveSmokeConverged(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair,
            int availableFiles,
            int expectedFiles,
            int hashMismatches,
            int readFailures)
        {
            bool pairsIdle = IsSuccessfullySyncedIdlePair(firstPair)
                && IsSuccessfullySyncedIdlePair(secondPair);
            bool filesConverged = availableFiles == expectedFiles
                && hashMismatches == 0
                && readFailures == 0;
            return pairsIdle && filesConverged;
        }

        private static string FormatLiveSmokeConvergenceDetails(
            DesktopSyncPairSnapshot? firstPair,
            DesktopSyncPairSnapshot? secondPair,
            int availableFiles,
            int expectedFiles,
            int hashMismatches,
            int readFailures)
        {
            return "availableSeedFiles="
                + availableFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + expectedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", hashMismatches=" + hashMismatches.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", readFailures=" + readFailures.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", firstStatus=" + (firstPair?.Status ?? "<missing>")
                + ", secondStatus=" + (secondPair?.Status ?? "<missing>");
        }

        private static IReadOnlyDictionary<string, string> BuildExpectedLiveSmokeHashes(
            IEnumerable<string> localRoots,
            IEnumerable<LiveSyncSmokeSeededLocalFile> seededLocalFiles)
        {
            Dictionary<string, string> expectedHashes = new(StringComparer.OrdinalIgnoreCase);
            foreach (LiveSyncSmokeSeededLocalFile file in seededLocalFiles)
            {
                foreach (string localRoot in localRoots)
                {
                    string fullPath = FullPath(localRoot, file.RelativePath);
                    if (File.Exists(fullPath))
                    {
                        expectedHashes[fullPath] = file.Sha256;
                    }
                }
            }

            return expectedHashes;
        }

        private static (int AvailableFiles, int HashMismatches, int ReadFailures) EvaluateLiveSmokeHashes(
            IReadOnlyDictionary<string, string> expectedHashes,
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> hashReads)
        {
            int availableFiles = 0;
            int hashMismatches = 0;
            int readFailures = 0;
            foreach ((string fullPath, string expectedHash) in expectedHashes)
            {
                if (!hashReads.TryGetValue(fullPath, out LiveSyncSmokeFileHashReadResult? read)
                    || read.Sha256 is null)
                {
                    readFailures++;
                    continue;
                }

                if (string.Equals(read.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    availableFiles++;
                }
                else
                {
                    hashMismatches++;
                }
            }

            return (availableFiles, hashMismatches, readFailures);
        }

        private static bool IsSuccessfullySyncedIdlePair(DesktopSyncPairSnapshot? pair)
        {
            return pair is not null
                && string.Equals(pair.Status, "Idle", StringComparison.Ordinal)
                && pair.LastSyncedAtUtc.HasValue
                && pair.LastError is null;
        }

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
            var files = new[]
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

        private static async Task<int> VerifySeededLocalFilesAsync(
            IReadOnlyList<LiveSyncSmokeSeededLocalFile> files,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            if (files.Count == 0)
            {
                return 0;
            }

            List<string> failures = [];
            foreach (LiveSyncSmokeSeededLocalFile file in files)
            {
                if (!File.Exists(file.FullPath))
                {
                    failures.Add(file.RelativePath + "=missing");
                    continue;
                }

                string actualHash = await ComputeFileSha256Async(file.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(file.RelativePath + "=sha256-mismatch:" + actualHash);
                }
            }

            bool passed = failures.Count == 0;
            await output.WriteLineAsync(
                FormatCheck(passed, label)
                + " files=" + files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (passed ? string.Empty : ", " + string.Join(", ", failures))).ConfigureAwait(false);
            return passed ? 0 : 1;
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

        private static async Task<TextReadSnapshot> TryReadAllTextForLiveSmokeAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
            {
                return new TextReadSnapshot(false, false, null, string.Empty);
            }

            try
            {
                string content = await ReadAllTextThroughExternalProcessAsync(filePath, cancellationToken)
                    .ConfigureAwait(false);
                return new TextReadSnapshot(true, true, content, string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new TextReadSnapshot(true, false, null, CleanSingleLine(exception.Message));
            }
        }

        private static async Task<string> ReadAllTextThroughExternalProcessAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadAllBytesThroughExternalProcessAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF'
                ? text[1..]
                : text;
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
                timeout: null,
                cancellationToken)
                .ConfigureAwait(false);
            return Convert.FromBase64String(base64.Trim());
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

        private static async Task<int> CountStateEntriesAsync(
            DesktopAppPaths paths,
            Guid syncPairId,
            CancellationToken cancellationToken)
        {
            var stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncStateEntry> entries = await stateStore
                .LoadPairAsync(syncPairId.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            return entries.Count;
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

        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
        }

        private static string FullPath(string localRoot, string relativePath)
        {
            return Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool IsSameOrNestedPath(string firstPath, string secondPath)
        {
            string first = NormalizeFullPath(firstPath);
            string second = NormalizeFullPath(secondPath);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(first, second, comparison)
                || second.StartsWith(EnsureTrailingSeparator(first), comparison)
                || first.StartsWith(EnsureTrailingSeparator(second), comparison);
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, comparison))
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

        private readonly record struct PresenceSnapshot(bool Passed, string Details);

        private readonly record struct RenameSnapshot(bool Passed, string Details);

        private readonly record struct AbsentSnapshot(bool Passed, string Details);

        private readonly record struct TextReadSnapshot(bool Exists, bool Read, string? Content, string Details);

        private readonly record struct LiveSyncSmokeConvergenceSnapshot(bool Passed, string Details);

        private record ShellShareLinkSmokeData(
            string SyncedFilePath,
            string RemoteOnlyPlaceholderPath,
            string HydratedPlaceholderPath,
            string DirectoryPath,
            string LocalOnlyPath);

        private record LiveSyncSmokeSeededLocalFile(string FullPath, string RelativePath, string Sha256);

        private class ShellShareLinkSmokeClient : IDesktopShellShareLinkClient
        {
            private readonly DesktopShellShareLinkResult _result;

            public ShellShareLinkSmokeClient(DesktopShellShareLinkResult result)
            {
                _result = result;
            }

            public Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
                ShellShareLinkTarget target,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_result);
            }
        }

        private class ShellShareLinkSmokeClipboardService : IDesktopClipboardService
        {
            public string? CopiedText { get; private set; }

            public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopiedText = text;
                return Task.CompletedTask;
            }
        }

        private class ShellShareLinkSmokeNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => true;

            public string? LastMessage { get; private set; }

            public void Show(string title, string message)
            {
                LastMessage = message;
            }
        }

        private class SocketCleanupSmokeTraceListener : TraceListener
        {
            private readonly StringWriter _writer = new();

            public string Output => _writer.ToString();

            public override void Write(string? message)
            {
                _writer.Write(message);
            }

            public override void WriteLine(string? message)
            {
                _writer.WriteLine(message);
            }
        }

        private class LiveSmokePlatformCommandService(TextWriter output, TimeSpan approvalHold) : IPlatformCommandService
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
                        + approvalHold.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds before polling so the approval page can load.").ConfigureAwait(false);
                    await Task.Delay(approvalHold, cancellationToken).ConfigureAwait(false);
                }

                await output.WriteLineAsync("Waiting for browser approval...").ConfigureAwait(false);
            }
        }
    }
}
