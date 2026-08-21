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
                    or "server-url-session-mismatch"
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
    }
}
