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

        private static DesktopShellController CreateUpdateSmokeController(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            IDesktopUpdateService? updateService = null,
            IDesktopUpdateInstaller? updateInstaller = null)
        {
            DesktopTraceLoggerFactory loggerFactory = new();
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
    }
}
