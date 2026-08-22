// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using AppRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        private async Task AddConnectivitySelfTestsAsync(
            DesktopSelfTestRun run,
            CancellationToken cancellationToken)
        {
            Uri? serverUrl = _startupOptions.ServerUrl ?? run.Preferences?.RememberedServerUrl;
            await AddServerIdentitySelfTestAsync(run.Items, serverUrl, cancellationToken).ConfigureAwait(false);
            await AddChangeFeedSelfTestAsync(run.Items, serverUrl, cancellationToken).ConfigureAwait(false);
        }

        private async Task AddServerIdentitySelfTestAsync(
            List<DesktopSelfTestItemSnapshot> items,
            Uri? serverUrl,
            CancellationToken cancellationToken)
        {
            if (serverUrl is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot("Server identity", true, "Not configured"));
                return;
            }

            await AddSelfTestCheckAsync(
                items,
                "Server identity",
                async () =>
                {
                    DesktopServerProbeResult result = await ProbeServerAsync(
                        serverUrl.AbsoluteUri,
                        cancellationToken).ConfigureAwait(false);
                    if (!result.IsCottonServer)
                    {
                        throw new InvalidOperationException("Cotton server not found.");
                    }

                    return result.Product ?? "Cotton Cloud";
                }).ConfigureAwait(false);
        }

        private async Task AddChangeFeedSelfTestAsync(
            List<DesktopSelfTestItemSnapshot> items,
            Uri? serverUrl,
            CancellationToken cancellationToken)
        {
            DesktopSyncApplicationHost? activeHost = _host;
            if (serverUrl is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot(
                    "Desktop sync change feed",
                    false,
                    "Not configured",
                    Skipped: true));
                return;
            }

            if (activeHost is null)
            {
                items.Add(new DesktopSelfTestItemSnapshot(
                    "Desktop sync change feed",
                    false,
                    "Sign in to verify",
                    Skipped: true));
                return;
            }

            await AddSelfTestCheckAsync(
                items,
                "Desktop sync change feed",
                () => CheckSyncChangeFeedAsync(activeHost, cancellationToken)).ConfigureAwait(false);
        }

        private async Task AddSyncPairSelfTestsAsync(
            DesktopSelfTestRun run,
            CancellationToken cancellationToken)
        {
            foreach (SyncPairSettings syncPair in run.SyncPairs)
            {
                await AddSelfTestCheckAsync(
                    run.Items,
                    "Local root: " + syncPair.DisplayName,
                    () => CheckLocalRootAsync(syncPair, cancellationToken)).ConfigureAwait(false);
                DesktopSyncApplicationHost? host = _host;
                if (host is null)
                {
                    run.Items.Add(new DesktopSelfTestItemSnapshot(
                        "Remote root: " + syncPair.DisplayName,
                        false,
                        "Sign in to verify",
                        Skipped: true));
                }
                else
                {
                    await AddSelfTestCheckAsync(
                        run.Items,
                        "Remote root: " + syncPair.DisplayName,
                        () => CheckRemoteRootAsync(host, syncPair, cancellationToken)).ConfigureAwait(false);
                }
            }
        }

        private async Task<string> CheckAuthenticationStateAsync(CancellationToken cancellationToken)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is not null)
            {
                TokenPairDto? activeTokens = await host.TokenStore.GetAsync(cancellationToken).ConfigureAwait(false);
                if (activeTokens is null)
                {
                    throw new InvalidOperationException("Signed in session has no stored token pair.");
                }

                return "Signed in";
            }

            FileCottonTokenStore tokenStore = new(_paths.TokenStorePath);
            TokenPairDto? storedTokens = await tokenStore.GetAsync(cancellationToken).ConfigureAwait(false);
            return storedTokens is null ? "Signed out" : "Stored session available";
        }

        private static Task<string> CheckFileWatcherAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(Path.GetTempPath(), "cotton-sync-watcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using FileSystemWatcher watcher = new(directory)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                return Task.FromResult("Available");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static Task<string> CheckDesktopIconAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-192.png");
            if (!File.Exists(iconPath))
            {
                throw new FileNotFoundException("Desktop icon asset was not found.", iconPath);
            }

            return Task.FromResult(iconPath);
        }

        private static async Task<string> CheckUpdateCacheAsync(
            string updateCacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(updateCacheDirectory);
            string probePath = Path.Combine(updateCacheDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probePath, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probePath);
            return updateCacheDirectory;
        }

        private static async Task<string> CheckSyncChangeFeedAsync(
            DesktopSyncApplicationHost host,
            CancellationToken cancellationToken)
        {
            SyncChangesResponseDto response = await host.Sync.GetChangesAsync(sinceCursor: 0, limit: 1, cancellationToken)
                .ConfigureAwait(false);
            return "Ready; next cursor " + response.NextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<string> CheckRemoteRootAsync(
            DesktopSyncApplicationHost host,
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            _ = await host.Nodes.GetAsync(syncPair.RemoteRootNodeId, cancellationToken).ConfigureAwait(false);
            return syncPair.RemoteRootNodeId.ToString();
        }

        private static async Task AddSelfTestCheckAsync(
            List<DesktopSelfTestItemSnapshot> items,
            string name,
            Func<Task<string>> checkAsync)
        {
            try
            {
                string details = await checkAsync().ConfigureAwait(false);
                items.Add(new DesktopSelfTestItemSnapshot(name, true, details));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Desktop self-test check failed for {0}: {1}", name, exception);
                items.Add(new DesktopSelfTestItemSnapshot(
                    name,
                    false,
                    DesktopActionRequiredMessageResolver.FromException(exception)));
            }
        }
    }
}
