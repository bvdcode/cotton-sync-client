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
        private async Task SignInAsync()
        {
            IsBusy = true;
            try
            {
                AuthSession session = await _controller.SignInAsync(
                    new DesktopSignInRequest(ServerUrl, Username, Password, TotpCode)).ConfigureAwait(true);
                ApplySignedInSession(session, "Signed in");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanRetryStoredSession()
        {
            return !IsBusy
                && !IsSignedIn
                && HasStoredSession
                && !string.IsNullOrWhiteSpace(ServerUrl);
        }

        private async Task RetryStoredSessionAsync()
        {
            Task previousRetry = CancelStoredSessionRetry();
            await StoredSessionRestoreRetryCoordinator.IgnoreCancellationAsync(previousRetry).ConfigureAwait(true);
            IsBusy = true;
            GlobalStatus = "Reconnecting";
            try
            {
                DesktopStoredSessionRestoreSnapshot result = await _controller
                    .RestoreStoredSessionAsync(ServerUrl)
                    .ConfigureAwait(true);
                ApplyStoredSessionRestoreResult(result, "Session restored");
            }
            finally
            {
                IsBusy = false;
                BeginStoredSessionRetry();
            }
        }

        private void ApplyStoredSessionRestoreResult(
            DesktopStoredSessionRestoreSnapshot result,
            string activityDetails)
        {
            HasStoredSession = result.HasStoredSession;
            if (result.Session is not null)
            {
                ApplySignedInSession(result.Session, activityDetails);
                return;
            }

            GlobalStatus = result.HasStoredSession ? "Waiting to reconnect" : "Session expired";
            if (result.HasStoredSession)
            {
                StoredSessionRestoreMessage = result.ErrorMessage
                    ?? "Saved session is temporarily unavailable. Cotton Sync will retry automatically.";
                ActionRequiredMessage = string.Empty;
                return;
            }

            StoredSessionRestoreMessage = string.Empty;
            ActionRequiredMessage = result.ErrorMessage
                ?? "Saved session expired. Sign in again to continue syncing.";
        }

        private void BeginStoredSessionRetry()
        {
            if (IsSignedIn || !HasStoredSession || string.IsNullOrWhiteSpace(ServerUrl))
            {
                return;
            }

            _storedSessionRetryCoordinator.Begin(ServerUrl);
        }

        private Task CancelStoredSessionRetry()
        {
            return _storedSessionRetryCoordinator.Cancel();
        }

        private async Task SignInWithBrowserAsync()
        {
            using CancellationTokenSource cancellation = new();
            _browserSignInCancellation = cancellation;
            IsBrowserSignInPending = true;
            BrowserSignInStatus = "Approve this sign-in in your browser.";
            IsBusy = true;
            GlobalStatus = "Waiting for browser sign-in";
            ActionRequiredMessage = string.Empty;
            try
            {
                AuthSession session = await _controller.SignInWithBrowserAsync(ServerUrl, cancellation.Token)
                    .ConfigureAwait(true);
                ApplySignedInSession(session, "Signed in with browser");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                GlobalStatus = "Sign-in cancelled";
                BrowserSignInStatus = string.Empty;
                AddActivity("Account", string.Empty, "Browser sign-in cancelled");
            }
            finally
            {
                if (ReferenceEquals(_browserSignInCancellation, cancellation))
                {
                    _browserSignInCancellation = null;
                }

                IsBrowserSignInPending = false;
                BrowserSignInStatus = string.Empty;
                IsBusy = false;
            }
        }

        private Task CancelBrowserSignInAsync()
        {
            _browserSignInCancellation?.Cancel();
            BrowserSignInStatus = "Cancelling browser sign-in.";
            GlobalStatus = "Cancelling sign-in";
            return Task.CompletedTask;
        }

        private void ApplySignedInSession(AuthSession session, string activityDetails)
        {
            CancelStoredSessionRetry();
            HasStoredSession = true;
            StoredSessionRestoreMessage = string.Empty;
            IsSignedIn = true;
            AccountName = ResolveAccountDisplayName(session.Email, session.Username);
            Username = AccountName;
            Password = string.Empty;
            TotpCode = string.Empty;
            GlobalStatus = "Connected";
            ActionRequiredMessage = string.Empty;
            AddActivity("Account", AccountName, activityDetails);
            ShowNativeNotification("Signed in", AccountName);
            RefreshDiagnosticsItems();
        }

        private async Task SignOutAsync()
        {
            IsBusy = true;
            try
            {
                await _controller.SignOutAsync().ConfigureAwait(true);
                ApplySignedOutState("Signed out");
                AddActivity("Account", string.Empty, "Signed out");
                ShowNativeNotification("Signed out", "Cotton Sync is signed out.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplySignedOutState(string globalStatus)
        {
            CancelStoredSessionRetry();
            HasStoredSession = false;
            StoredSessionRestoreMessage = string.Empty;
            IsSignedIn = false;
            AccountName = "Signed out";
            GlobalStatus = globalStatus;
            Password = string.Empty;
            TotpCode = string.Empty;
            IsAddSyncPairWizardVisible = false;
            IsSettingsVisible = false;
            IsSelectedSyncPairEditorVisible = false;
            ActionRequiredMessage = string.Empty;
            Notifications.Clear();
            _notificationTracker.Reset();
            RemoteFolders.Clear();
            ClearRunProgress();
            ClearTransferProgress();
            foreach (SyncPairRowViewModel syncPair in SyncPairs)
            {
                ClearSyncPairProgress(syncPair);
            }

            SetAllPairStatuses("Idle");
            RefreshCurrentProgressText();
            RefreshDiagnosticsItems();
        }
    }
}
