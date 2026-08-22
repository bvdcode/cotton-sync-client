// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.Auth;
using Cotton.Sync.State;
using System.Net;

namespace Cotton.Sync.Cli
{
    public static partial class SyncCliCommandRunner
    {

        private static bool IsSupportableSyncOnceException(Exception exception)
        {
            return exception is CottonApiException
                or HttpRequestException
                or IOException
                or TimeoutException
                or TaskCanceledException
                or UnauthorizedAccessException
                or SyncPathValidationException;
        }


        private static bool IsRetriableSyncOnceException(Exception exception)
        {
            return exception switch
            {
                CottonApiException apiException => IsTransientStatusCode(apiException.StatusCode),
                HttpRequestException requestException => IsTransientStatusCode(requestException.StatusCode),
                TimeoutException => true,
                TaskCanceledException => true,
                _ => false,
            };
        }


        private static bool IsRetriableBrowserSignInException(AppCodeBrowserSignInException exception)
        {
            return string.Equals(exception.Error, "network_unavailable", StringComparison.OrdinalIgnoreCase);
        }


        private static TimeSpan GetSyncOnceRetryDelay(int completedAttempts)
        {
            if (SyncOnceInitialRetryDelay == TimeSpan.Zero || SyncOnceMaxRetryDelay == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            double multiplier = Math.Pow(2, Math.Max(0, completedAttempts - 1));
            double milliseconds = Math.Min(
                SyncOnceInitialRetryDelay.TotalMilliseconds * multiplier,
                SyncOnceMaxRetryDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds);
        }


        private static string FormatRetryDelay(TimeSpan delay)
        {
            if (delay.TotalMilliseconds < 1000)
            {
                return delay.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "ms";
            }

            return delay.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }


        private static async Task WriteSyncOnceFailureAsync(
            TextWriter error,
            SyncCliConnectionOptions options,
            Exception exception)
        {
            await error.WriteLineAsync("sync-once failed.").ConfigureAwait(false);
            await error.WriteLineAsync("Server: " + options.ServerUri).ConfigureAwait(false);
            await error.WriteLineAsync("Local root: " + options.LocalRoot).ConfigureAwait(false);
            await error.WriteLineAsync("Remote root: " + FormatRemoteRoot(options)).ConfigureAwait(false);
            await error.WriteLineAsync("Sync pair: " + options.SyncPairId).ConfigureAwait(false);
            await error.WriteLineAsync("Database: " + options.DatabasePath).ConfigureAwait(false);
            await error.WriteLineAsync("Error: " + FormatSyncOnceFailure(exception)).ConfigureAwait(false);
        }


        private static string FormatSyncOnceFailure(Exception exception)
        {
            if (exception is CottonApiException apiException)
            {
                HttpStatusCode apiStatusCode = apiException.StatusCode.GetValueOrDefault();
                return "Cotton API returned "
                    + ((int)apiStatusCode).ToStringInvariant()
                    + " "
                    + apiStatusCode
                    + ". "
                    + CleanSingleLine(apiException.Message);
            }

            if (exception is HttpRequestException httpException && httpException.StatusCode is HttpStatusCode statusCode)
            {
                return "HTTP request failed with "
                    + ((int)statusCode).ToStringInvariant()
                    + " "
                    + statusCode
                    + ". "
                    + CleanSingleLine(httpException.Message);
            }

            return CleanSingleLine(exception.Message);
        }


        private static string FormatRemoteRoot(SyncCliConnectionOptions options)
        {
            return options.RemoteRootNodeId?.ToString("D") ?? options.RemoteRootPath ?? "<not resolved>";
        }


        private static bool IsTransientStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode is null
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
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


        private static bool IsHelp(string value)
        {
            return string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);
        }


        private static bool IsVersion(string value)
        {
            return string.Equals(value, "--version", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "-v", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "version", StringComparison.OrdinalIgnoreCase);
        }


        private static Task WriteHelpAsync(TextWriter writer)
        {
            return writer.WriteLineAsync(
                """
                Cotton Sync CLI

                Commands:
                  auth-browser --server <url-or-host>
                      [--application-name <name>] [--application-version <version>]
                      [--device-name <name>] [--timeout-seconds <seconds>]
                      Verifies app-code browser sign-in, then revokes the temporary session.

                  state-summary --database <path> --sync-pair <id>
                      Initializes and summarizes a sync-state SQLite database for one sync pair.
                  sync-once --server <url-or-host> --username <name>
                      (--password <password> | --password-env <name>) --local-root <path>
                      (--remote-root <node-id> | --remote-path <path>)
                      --sync-pair <id> --database <path>
                      [--two-factor-code <code>]
                  sync-once --server <url-or-host> --browser-login --local-root <path>
                      (--remote-root <node-id> | --remote-path <path>)
                      --sync-pair <id> --database <path>
                      Signs in and runs one full-mirror sync pass for one pair.
                  sync-soak --server <url-or-host> --username <name>
                      (--password <password> | --password-env <name>) --local-root <path>
                      (--remote-root <node-id> | --remote-path <path>)
                      --sync-pair <id> --database <path>
                      (--iterations <count> | --duration-seconds <seconds>)
                      [--interval-seconds <seconds>] [--probe-file <relative-path>]
                      [--second-local-root <path> --second-sync-pair <id>
                       --second-database <path>]
                      [--two-factor-code <code>]
                  sync-soak --server <url-or-host> --browser-login --local-root <path>
                      (--remote-root <node-id> | --remote-path <path>)
                      --sync-pair <id> --database <path>
                      (--iterations <count> | --duration-seconds <seconds>)
                      [--interval-seconds <seconds>] [--probe-file <relative-path>]
                      [--second-local-root <path> --second-sync-pair <id>
                       --second-database <path>]
                      Repeats full-mirror sync passes for one-client or two-client
                      release soak validation.
                  sync-crud-smoke --server <url-or-host>
                      (--username <name> (--password <password> | --password-env <name>) | --browser-login)
                      --local-root <path> (--remote-root <node-id> | --remote-path <path>)
                      --sync-pair <id> --database <path>
                      --second-local-root <path> --second-sync-pair <id>
                      --second-database <path> [--two-factor-code <code>]
                      Runs a two-client create, download, rename, delete, and final
                      convergence smoke against one full-mirror remote root.
                """);
        }
    }
}
