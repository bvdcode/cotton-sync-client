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
        private string ResolveCurrentSyncPairActionRequiredMessage()
        {
            if (SyncPairs.Count == 0)
            {
                return string.Empty;
            }

            DesktopSyncPairStatusSnapshot[] pairStatuses = SyncPairs
                .Select(static pair => new DesktopSyncPairStatusSnapshot(
                    pair.Id,
                    pair.Status,
                    pair.LastError,
                    pair.CurrentOperation,
                    pair.LastSyncedAtUtc))
                .ToArray();

            return DesktopActionRequiredMessageResolver.FromStatus(new DesktopSyncStatusSnapshot(pairStatuses));
        }

        private bool TryResolveRemoteMassDeleteApproval(
            out Guid syncPairId,
            out RemoteDeletePlanApproval approval)
        {
            syncPairId = Guid.Empty;
            approval = null!;
            bool found = false;
            foreach (SyncPairRowViewModel pair in SyncPairs)
            {
                if (!DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteApproval(
                        pair.LastError,
                        out RemoteDeletePlanApproval pairApproval))
                {
                    continue;
                }

                if (found)
                {
                    syncPairId = Guid.Empty;
                    approval = null!;
                    return false;
                }

                found = true;
                syncPairId = pair.Id;
                approval = pairApproval;
            }

            return found;
        }

        private bool HasRemoteMassDeleteGuard()
        {
            return SyncPairs.Any(static pair =>
                DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteCount(pair.LastError, out _));
        }
    }
}
