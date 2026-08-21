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
            SqliteSyncPairSettingsStore syncPairs = new(paths.AppDatabasePath);
            await syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SqliteSyncStateStore syncState = new(paths.SyncStateDatabasePath);
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

        private static string FormatSelfTestItem(DesktopSelfTestItemSnapshot item)
        {
            string status = item.Skipped ? "SKIP" : item.Passed ? "OK" : "FAIL";
            return "[" + status + "] " + item.Name + " - " + item.Details;
        }

        private static string FormatBoolean(bool value)
        {
            return value ? "true" : "false";
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

        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
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
    }
}
