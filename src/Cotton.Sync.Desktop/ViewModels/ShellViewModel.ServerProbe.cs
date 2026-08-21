// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private async Task ProbeServerAfterDelayAsync(
            string serverUrl,
            CancellationTokenSource probeCancellation)
        {
            CancellationToken cancellationToken = probeCancellation.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
                DesktopServerProbeResult result = await ProbeServerWithRetriesAsync(
                        serverUrl,
                        probeCancellation)
                    .ConfigureAwait(false);
                await _uiDispatcher.InvokeAsync(
                    () =>
                    {
                        if (!IsCurrentServerProbe(serverUrl, probeCancellation))
                        {
                            return;
                        }

                        ApplyServerProbeResult(result);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Failed to probe Cotton server {0}: {1}", serverUrl, exception);
                await _uiDispatcher.InvokeAsync(
                    () =>
                    {
                        if (!IsCurrentServerProbe(serverUrl, probeCancellation))
                        {
                            return;
                        }

                        ApplyServerProbeFailure(exception);
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task<DesktopServerProbeResult> ProbeServerWithRetriesAsync(
            string serverUrl,
            CancellationTokenSource probeCancellation)
        {
            CancellationToken cancellationToken = probeCancellation.Token;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= ServerProbeMaxAttempts; attempt++)
            {
                try
                {
                    return await _controller.ProbeServerAsync(serverUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsTransientServerProbeFailure(exception) && attempt < ServerProbeMaxAttempts)
                {
                    lastException = exception;
                    Trace.TraceWarning("Cotton server probe attempt {0} failed for {1}: {2}", attempt, serverUrl, exception);
                    await _uiDispatcher.InvokeAsync(
                        () =>
                        {
                            if (IsCurrentServerProbe(serverUrl, probeCancellation))
                            {
                                ServerProbeStatus = "Connection blocked or unavailable; retrying";
                            }
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    TimeSpan retryDelay = TimeSpan.FromMilliseconds(
                        ServerProbeInitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw lastException ?? new InvalidOperationException("Cotton server probe failed.");
        }

        private void ApplyServerProbeFailure(Exception exception)
        {
            IsServerProbeChecking = false;
            IsServerVerified = false;
            IsServerProbeFailed = true;
            ServerProbeStatus = IsTransientServerProbeFailure(exception)
                ? "Cannot reach server. Check network or firewall."
                : "Cotton server not found";
        }

        private void ApplyServerProbeResult(DesktopServerProbeResult result)
        {
            IsServerProbeChecking = false;
            if (result.IsCottonServer)
            {
                SetDesktopSyncChangesApiUnavailable(false);
                ApplyNormalizedServerUrl(result.ServerUrl);
            }

            IsServerVerified = result.IsCottonServer;
            IsServerProbeFailed = !result.IsCottonServer;
            ServerProbeStatus = result.IsCottonServer
                ? "Cotton Cloud"
                : "Cotton server not found";
        }

        private void ApplyNormalizedServerUrl(Uri serverUrl)
        {
            if (SetProperty(ref _serverUrl, serverUrl.AbsoluteUri, nameof(ServerUrl)))
            {
                SignInCommand.RaiseCanExecuteChanged();
                RefreshDiagnosticsItems();
            }
        }

        private void ResetServerProbe()
        {
            SetDesktopSyncChangesApiUnavailable(false);
            IsServerProbeChecking = false;
            IsServerVerified = false;
            IsServerProbeFailed = false;
            ServerProbeStatus = string.Empty;
        }

        private void SetDesktopSyncChangesApiUnavailable(bool isUnavailable)
        {
            if (_isDesktopSyncChangesApiUnavailable == isUnavailable)
            {
                return;
            }

            _isDesktopSyncChangesApiUnavailable = isUnavailable;
            RaiseAddSyncPairFlowCommandStates();
        }

        private static bool IsMissingDesktopSyncChangesApiMessage(string message)
        {
            return DesktopActionRequiredMessageResolver.IsMissingDesktopSyncChangesApi(message);
        }

        private static bool HasMissingDesktopSyncChangesApiFailure(DesktopSelfTestSnapshot selfTest)
        {
            return selfTest.Items.Any(static item =>
                !item.Skipped && DesktopActionRequiredMessageResolver.IsMissingDesktopSyncChangesApi(item.Details));
        }

        private static bool IsTransientServerProbeFailure(Exception exception)
        {
            return exception is HttpRequestException
                or IOException
                or TimeoutException
                or TaskCanceledException
                || ContainsSocketException(exception);
        }

        private static bool ContainsSocketException(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SocketException)
                {
                    return true;
                }
            }

            return false;
        }

        private void ScheduleServerProbe(string serverUrl)
        {
            _serverProbeCancellation?.Cancel();
            _serverProbeCancellation?.Dispose();
            string normalized = serverUrl.Trim();
            if (normalized.Length == 0)
            {
                _serverProbeCancellation = null;
                ResetServerProbe();
                return;
            }

            _serverProbeCancellation = new CancellationTokenSource();
            IsServerProbeChecking = true;
            IsServerVerified = false;
            IsServerProbeFailed = false;
            ServerProbeStatus = "Checking server";
            _ = ProbeServerAfterDelayAsync(normalized, _serverProbeCancellation);
        }

        private bool IsCurrentServerProbe(string serverUrl, CancellationTokenSource probeCancellation)
        {
            return ReferenceEquals(_serverProbeCancellation, probeCancellation)
                && !probeCancellation.IsCancellationRequested
                && string.Equals(ServerUrl.Trim(), serverUrl, StringComparison.Ordinal);
        }
    }
}
