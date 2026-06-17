// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.State;
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
        private static readonly TimeSpan DesktopLocalQuietWindow = TimeSpan.FromMilliseconds(2300);
        private static readonly TimeSpan PropagationTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PropagationPollInterval = TimeSpan.FromSeconds(1);

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
            string bundlePath = await controller.ExportDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("Cotton Sync Desktop diagnostics").ConfigureAwait(false);
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
            IReadOnlyList<SyncPairSettings> configuredPairs = await syncPairs
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
            IWindowsCloudFilesAdapter cloudFiles = cloudFilesAdapter ?? new WindowsCloudFilesAdapter();
            int cleaned = 0;
            int failures = 0;

            await output.WriteLineAsync("Cotton Sync Desktop Cloud Files cleanup").ConfigureAwait(false);
            foreach (SyncPairSettings syncPair in configuredPairs.Where(static pair => pair.Mode == SyncPairMode.WindowsVirtualFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    cloudFiles.UnregisterSyncRoot(syncPair);
                    cleaned++;
                    await output.WriteLineAsync("Unregistered: " + syncPair.LocalRootPath).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    await output
                        .WriteLineAsync("Failed: " + syncPair.LocalRootPath + " - " + CleanSingleLine(exception.Message))
                        .ConfigureAwait(false);
                }
            }

            IWindowsStorageProviderSyncRootRegistrar? registrar =
                storageProviderRegistrar ?? WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            if (registrar is not null)
            {
                try
                {
                    if (registrar.IsSupported())
                    {
                        registrar.UnregisterAllForCurrentUser();
                        await output
                            .WriteLineAsync("Orphaned storage-provider roots cleaned.")
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await output
                            .WriteLineAsync("Orphaned storage-provider cleanup skipped: Windows StorageProvider is unavailable.")
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    await output
                        .WriteLineAsync("Failed orphaned storage-provider cleanup: " + CleanSingleLine(exception.Message))
                        .ConfigureAwait(false);
                }
            }

            await output.WriteLineAsync("Roots cleaned: " + cleaned.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
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

            int failures = 0;
            bool firstSignedIn = false;
            bool secondSignedIn = false;
            SyncPairSettings? firstPair = null;
            SyncPairSettings? secondPair = null;
            try
            {
                await output.WriteLineAsync("Cotton Sync Desktop live sync smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Server: " + startupOptions.ServerUrl).ConfigureAwait(false);
                await output.WriteLineAsync("Remote root: " + startupOptions.RemotePath).ConfigureAwait(false);
                await output.WriteLineAsync("Local root: " + startupOptions.LocalRoot).ConfigureAwait(false);
                await output.WriteLineAsync("Second local root: " + startupOptions.SecondLocalRoot).ConfigureAwait(false);
                await output.WriteLineAsync("Sync mode: " + startupOptions.SyncMode).ConfigureAwait(false);
                await output.WriteLineAsync("Data root: " + paths.DataDirectory).ConfigureAwait(false);

                await output.WriteLineAsync("Approving first desktop client...").ConfigureAwait(false);
                await firstController.SignInWithBrowserAsync(
                    startupOptions.ServerUrl!.AbsoluteUri,
                    cancellationToken).ConfigureAwait(false);
                firstSignedIn = true;
                await output.WriteLineAsync("Approving second desktop client...").ConfigureAwait(false);
                await secondController.SignInWithBrowserAsync(
                    startupOptions.ServerUrl.AbsoluteUri,
                    cancellationToken).ConfigureAwait(false);
                secondSignedIn = true;

                firstPair = await firstController.AddSyncPairAsync(
                    new DesktopSyncPairRequest(startupOptions.LocalRoot!, startupOptions.RemotePath!, startupOptions.SyncMode),
                    cancellationToken).ConfigureAwait(false);
                secondPair = await secondController.AddSyncPairAsync(
                    new DesktopSyncPairRequest(startupOptions.SecondLocalRoot!, startupOptions.RemotePath!, startupOptions.SyncMode),
                    cancellationToken).ConfigureAwait(false);

                failures += await VerifyIdleAsync(
                    firstController,
                    secondController,
                    firstPair.Id,
                    secondPair.Id,
                    "Initial desktop sync reached idle/up-to-date.",
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientACreateAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientBCreateAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientARenameAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientBRenameAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientADeleteAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);
                failures += await RunClientBDeleteAsync(
                    startupOptions,
                    firstController,
                    secondController,
                    output,
                    cancellationToken).ConfigureAwait(false);

                await RunFinalConvergenceAsync(firstController, secondController, cancellationToken)
                    .ConfigureAwait(false);
                int finalStateEntries = await CountStateEntriesAsync(firstPaths, firstPair.Id, cancellationToken)
                    .ConfigureAwait(false)
                    + await CountStateEntriesAsync(secondPaths, secondPair.Id, cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Final state entries: " + finalStateEntries.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                if (finalStateEntries != 0)
                {
                    failures++;
                }

                await output.WriteLineAsync("Converged: " + (failures == 0 ? "yes" : "no")).ConfigureAwait(false);
                await output.WriteLineAsync("Failures: " + failures.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
                return failures == 0 ? 0 : 1;
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
                if (firstSignedIn)
                {
                    await TrySignOutAsync(firstController, output, "first").ConfigureAwait(false);
                }

                if (secondSignedIn)
                {
                    await TrySignOutAsync(secondController, output, "second").ConfigureAwait(false);
                }
            }
        }

        private static string FormatSelfTestItem(DesktopSelfTestItemSnapshot item)
        {
            string status = item.Skipped ? "SKIP" : item.Passed ? "OK" : "FAIL";
            return "[" + status + "] " + item.Name + " - " + item.Details;
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
                startupOptions);
        }

        private static string? ValidateLiveSyncSmokeOptions(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions)
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

            if (Directory.Exists(paths.DataDirectory) && DataDirectoryHasUnexpectedEntries(paths.DataDirectory))
            {
                return "--data-dir must be empty or contain only the current smoke log for --live-sync-smoke.";
            }

            if (IsSameOrNestedPath(startupOptions.LocalRoot, startupOptions.SecondLocalRoot))
            {
                return "--local-root and --second-local-root must be different and non-nested.";
            }

            string? firstRootError = ValidateEmptyOrMissingDirectory(startupOptions.LocalRoot, "--local-root");
            return firstRootError ?? ValidateEmptyOrMissingDirectory(startupOptions.SecondLocalRoot, "--second-local-root");
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
            await RunFinalConvergenceAsync(firstController, secondController, cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot firstSnapshot = await firstController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopShellSnapshot secondSnapshot = await secondController.LoadAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncPairSnapshot? firstPair = firstSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == firstPairId);
            DesktopSyncPairSnapshot? secondPair = secondSnapshot.SyncPairs.FirstOrDefault(pair => pair.Id == secondPairId);
            bool passed = firstPair is not null
                && secondPair is not null
                && string.Equals(firstPair.Status, "Idle", StringComparison.Ordinal)
                && string.Equals(secondPair.Status, "Idle", StringComparison.Ordinal)
                && firstPair.LastError is null
                && secondPair.LastError is null;
            await output.WriteLineAsync(
                FormatCheck(passed, label)
                + " firstStatus=" + (firstPair?.Status ?? "<missing>")
                + ", secondStatus=" + (secondPair?.Status ?? "<missing>")).ConfigureAwait(false);
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
            File.Move(FullPath(startupOptions.LocalRoot!, LocalUploadPath), FullPath(startupOptions.LocalRoot!, LocalRenamedPath));
            return await WaitForRenameAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                LocalUploadPath,
                LocalRenamedPath,
                "Desktop local rename propagated to the second client.",
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
            File.Move(
                FullPath(startupOptions.SecondLocalRoot!, RemoteOriginPath),
                FullPath(startupOptions.SecondLocalRoot!, RemoteRenamedPath));
            return await WaitForRenameAsync(
                startupOptions.LocalRoot!,
                startupOptions.SecondLocalRoot!,
                RemoteOriginPath,
                RemoteRenamedPath,
                "Desktop remote-origin rename propagated to the first client.",
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
            bool firstExists = File.Exists(firstPath);
            bool secondExists = File.Exists(secondPath);
            string? firstContent = firstExists ? await File.ReadAllTextAsync(firstPath, cancellationToken).ConfigureAwait(false) : null;
            string? secondContent = secondExists ? await File.ReadAllTextAsync(secondPath, cancellationToken).ConfigureAwait(false) : null;
            bool firstMatches = string.Equals(firstContent, expectedContent, StringComparison.Ordinal);
            bool secondMatches = string.Equals(secondContent, expectedContent, StringComparison.Ordinal);
            bool passed = firstExists && secondExists && firstMatches && secondMatches;
            return new PresenceSnapshot(
                passed,
                "firstExists=" + firstExists
                + ", secondExists=" + secondExists
                + ", firstMatches=" + firstMatches
                + ", secondMatches=" + secondMatches);
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

        private sealed class LiveSmokePlatformCommandService(TextWriter output, TimeSpan approvalHold) : IPlatformCommandService
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
