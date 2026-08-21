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
    }
}
