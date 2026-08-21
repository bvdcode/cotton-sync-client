// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static bool DoesShellShareLinkCopyMatch(
            VfsShellShareLinkScenario scenario,
            int exitCode,
            VfsShellShareLinkSmokeClipboardService clipboard)
        {
            return scenario.ExpectCopied
                ? exitCode == 0 && !string.IsNullOrWhiteSpace(clipboard.CopiedText)
                : exitCode != 0 && clipboard.CopiedText is null;
        }

        private static bool DoesShellShareLinkFailureMatch(VfsShellShareLinkScenario scenario, string report)
        {
            return scenario.ExpectedFailureReason is null
                ? !report.Contains("FailureReason:", StringComparison.Ordinal)
                : report.Contains("FailureReason: " + scenario.ExpectedFailureReason, StringComparison.Ordinal);
        }

        private static bool DoesShellShareLinkNotificationMatch(
            VfsShellShareLinkScenario scenario,
            VfsShellShareLinkSmokeNotificationService notifications)
        {
            return scenario.ExpectCopied
                ? string.Equals(notifications.LastMessage, "Share link copied to clipboard.", StringComparison.Ordinal)
                : !string.IsNullOrWhiteSpace(notifications.LastMessage);
        }

        private static bool DoesShellShareLinkStatusMatch(VfsShellShareLinkScenario scenario, string report)
        {
            string expectedStatus = scenario.ExpectCopied ? "Status: resolved" : "Status: missing-baseline";
            return report.Contains(expectedStatus, StringComparison.Ordinal);
        }

        private static bool DoesShellShareLinkTargetMatch(
            VfsShellShareLinkScenario scenario,
            ShellShareLinkTarget? target)
        {
            if (!scenario.ExpectCopied)
            {
                return target is null;
            }

            return target is not null
                && string.Equals(
                    target.RelativePath,
                    SyncPath.Normalize(scenario.ExpectedRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                && target.Kind == scenario.ExpectedKind
                && HasExpectedShellShareLinkIdentity(scenario.ExpectedKind, target);
        }

        private static bool HasExpectedShellShareLinkIdentity(
            ShellShareLinkTargetKind expectedKind,
            ShellShareLinkTarget target)
        {
            return expectedKind switch
            {
                ShellShareLinkTargetKind.File => target.RemoteFileId.HasValue,
                ShellShareLinkTargetKind.Directory => target.RemoteNodeId.HasValue,
                ShellShareLinkTargetKind.Unknown => false,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedKind), expectedKind, null),
            };
        }

        private static bool DoesShellShareLinkReportHideLocalPath(
            VfsShellShareLinkScenario scenario,
            string report)
        {
            return !report.Contains(scenario.SelectedPath, StringComparison.OrdinalIgnoreCase)
                && !report.Contains(Path.GetFileName(scenario.SelectedPath), StringComparison.OrdinalIgnoreCase);
        }

        private static bool DoesShellShareLinkResultMatch(VfsShellShareLinkScenario scenario, string report)
        {
            string expectedResult = scenario.ExpectCopied ? "Result: passed" : "Result: failed";
            return report.Contains(expectedResult, StringComparison.Ordinal);
        }
    }
}
