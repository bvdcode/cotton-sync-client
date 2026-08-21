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
        private async Task PauseAsync()
        {
            IsSyncPausePending = true;
            GlobalStatus = "Pausing";
            ActionRequiredMessage = string.Empty;
            SetAllPairStatuses("Pausing", enabledOnly: true);
            RefreshCurrentProgressText();
            AddActivity("Sync", string.Empty, "Synchronization pause requested");
            try
            {
                await _controller.PauseAllAsync().ConfigureAwait(true);
                GlobalStatus = "Paused";
                SetAllPairStatuses("Paused", enabledOnly: true);
                RefreshCurrentProgressText();
                AddActivity("Sync", string.Empty, "Synchronization paused");
            }
            finally
            {
                IsSyncPausePending = false;
            }
        }

        private Task PauseResumeAsync()
        {
            return IsSyncPaused ? ResumeAsync() : PauseAsync();
        }

        private async Task ResumeAsync()
        {
            await _controller.ResumeAllAsync().ConfigureAwait(true);
            GlobalStatus = "Ready";
            ActionRequiredMessage = string.Empty;
            SetAllPairStatuses("Idle", enabledOnly: true);
            RefreshCurrentProgressText();
            AddActivity("Sync", string.Empty, "Synchronization resumed");
        }

        private async Task SyncNowAsync()
        {
            IsBusy = true;
            try
            {
                await _controller.SyncAllAsync().ConfigureAwait(true);
                string actionRequiredMessage = ResolveCurrentSyncPairActionRequiredMessage();
                if (!string.IsNullOrWhiteSpace(actionRequiredMessage))
                {
                    GlobalStatus = "Action required";
                    ActionRequiredMessage = actionRequiredMessage;
                    RefreshCurrentProgressText();
                    return;
                }

                GlobalStatus = "Checked for changes";
                ActionRequiredMessage = string.Empty;
                RefreshCurrentProgressText();
                AddActivity("Sync", string.Empty, "Manual sync completed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApproveRemoteMassDeleteAsync()
        {
            if (!TryResolveRemoteMassDeleteApproval(
                    out Guid syncPairId,
                    out RemoteDeletePlanApproval approval))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _controller
                    .SyncAllAsync(syncPairId: syncPairId, approvedRemoteDeletePlan: approval)
                    .ConfigureAwait(true);
                string actionRequiredMessage = ResolveCurrentSyncPairActionRequiredMessage();
                if (!string.IsNullOrWhiteSpace(actionRequiredMessage))
                {
                    GlobalStatus = "Action required";
                    ActionRequiredMessage = actionRequiredMessage;
                    RefreshCurrentProgressText();
                    return;
                }

                GlobalStatus = "Checked for changes";
                ActionRequiredMessage = string.Empty;
                RefreshCurrentProgressText();
                AddActivity("Sync", string.Empty, "Approved remote delete plan completed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SelfTestAsync()
        {
            IsBusy = true;
            try
            {
                DesktopSelfTestSnapshot result = await _controller.RunSelfTestAsync().ConfigureAwait(true);
                string selfTestActionRequiredMessage = DesktopActionRequiredMessageResolver.FromSelfTest(result);
                string syncPairActionRequiredMessage = ResolveCurrentSyncPairActionRequiredMessage();
                string actionRequiredMessage = string.IsNullOrWhiteSpace(selfTestActionRequiredMessage)
                    ? syncPairActionRequiredMessage
                    : selfTestActionRequiredMessage;
                SetDesktopSyncChangesApiUnavailable(HasMissingDesktopSyncChangesApiFailure(result));
                GlobalStatus = string.IsNullOrWhiteSpace(actionRequiredMessage) ? "Self-test passed" : "Action required";
                ActionRequiredMessage = actionRequiredMessage;
                SelfTestItems.Clear();
                foreach (DesktopSelfTestItemSnapshot item in result.Items)
                {
                    SelfTestItems.Add(new SelfTestItemRowViewModel
                    {
                        Name = item.Name,
                        Details = item.Details,
                        Passed = item.Passed,
                        Skipped = item.Skipped,
                    });
                    AddActivity(
                        item.Skipped ? "Info" : item.Passed ? "Check" : "Warning",
                        item.Name,
                        item.Skipped ? "Skipped: " + item.Details : item.Passed ? item.Details : "Failed: " + item.Details);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
