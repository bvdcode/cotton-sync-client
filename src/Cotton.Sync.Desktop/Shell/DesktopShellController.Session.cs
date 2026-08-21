// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
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
        public async Task<DesktopServerProbeResult> ProbeServerAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            Uri parsedServerUrl = ParseServerUrl(serverUrl);
            using HttpClient httpClient = DesktopHttpClientFactory.Create(_serverProbeTimeout);
            httpClient.BaseAddress = parsedServerUrl;
            PublicServerInfo? info;
            try
            {
                info = await httpClient
                    .GetFromJsonAsync<PublicServerInfo>(Routes.V1.Server + "/info", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Cotton server check timed out after "
                    + _serverProbeTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds.",
                    exception);
            }

            bool isCottonServer = string.Equals(info?.Product, Constants.ProductName, StringComparison.Ordinal);
            return new DesktopServerProbeResult(
                parsedServerUrl,
                isCottonServer,
                info?.Product,
                info?.InstanceIdHash);
        }

        public async Task<AuthSession> SignInAsync(
            DesktopSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Uri serverUrl = ParseServerUrl(request.ServerUrl);
            await EnsureReleaseSecureTokenStorageAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncApplicationHost host = _factory.Create(serverUrl);
            try
            {
                AuthSession session = await host.App.SignInAsync(
                    new PasswordSignInRequest
                    {
                        Username = request.Username.Trim(),
                        Password = request.Password,
                        TwoFactorCode = NormalizeOptional(request.TotpCode),
                        TrustDevice = true,
                    },
                    cancellationToken).ConfigureAwait(false);
                await CompleteSignInAsync(host, serverUrl, session, request.Username.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<AuthSession> SignInWithBrowserAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            Uri parsedServerUrl = ParseServerUrl(serverUrl);
            await EnsureReleaseSecureTokenStorageAsync(cancellationToken).ConfigureAwait(false);
            DesktopSyncApplicationHost host = _factory.Create(parsedServerUrl);
            try
            {
                AuthSession session = await host.App.SignInWithBrowserAsync(
                    new AppCodeBrowserSignInRequest
                    {
                        ApplicationName = "Cotton Sync Desktop",
                        ApplicationVersion = DesktopAppVersion.Current,
                        DeviceName = DesktopDeviceIdentity.CreateDeviceName(),
                    },
                    cancellationToken).ConfigureAwait(false);
                await CompleteSignInAsync(
                        host,
                        parsedServerUrl,
                        session,
                        session.Email ?? session.Username,
                        cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost? host = _host;
            if (host is null)
            {
                return;
            }

            try
            {
                await host.App.SignOutAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_host, host))
                {
                    DetachHost();
                }

                await host.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task CompleteSignInAsync(
            DesktopSyncApplicationHost host,
            Uri serverUrl,
            AuthSession session,
            string rememberedUsername,
            CancellationToken cancellationToken)
        {
            await _preferencesStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            AppPreferences preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(false);
            preferences.RememberedServerUrl = serverUrl;
            preferences.RememberedUsername = rememberedUsername;
            await TryApplyPreferredAutostartAsync(preferences, cancellationToken).ConfigureAwait(false);
            await host.App.SavePreferencesAsync(preferences, cancellationToken).ConfigureAwait(false);
            await ReplaceHostAsync(host, session, cancellationToken).ConfigureAwait(false);
            StartSessionSyncInBackground(host, "sign-in");
        }

        private static Uri ParseServerUrl(string serverUrl)
        {
            return DesktopServerUrl.NormalizeRequired(serverUrl, nameof(serverUrl));
        }
    }
}
