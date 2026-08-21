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
        private void HandleCommandError(Exception exception)
        {
            Trace.TraceError(exception.ToString());
            if (SyncFailureClassifier.IsTransientConnectionFailure(exception))
            {
                string message = ResolveTransientConnectionMessage(exception);
                GlobalStatus = "Offline";
                ActionRequiredMessage = string.Empty;
                AddActivity("Warning", string.Empty, message);
                RefreshCurrentProgressText();
                IsBusy = false;
                return;
            }

            GlobalStatus = ResolveCommandFailureStatus();
            string actionRequiredMessage = DesktopActionRequiredMessageResolver.FromException(exception);
            ActionRequiredMessage = actionRequiredMessage;
            AddActivity("Error", string.Empty, actionRequiredMessage);
            RefreshCurrentProgressText();
            IsBusy = false;
        }

        private static string ResolveTransientConnectionMessage(Exception exception)
        {
            if (exception is AggregateException aggregateException && aggregateException.InnerExceptions.Count == 1)
            {
                return DesktopActionRequiredMessageResolver.FromException(aggregateException.InnerExceptions[0]);
            }

            return exception is CottonApiException
                ? DesktopActionRequiredMessageResolver.FromException(exception)
                : DesktopActionRequiredMessageResolver.TemporaryServerUnavailableMessage;
        }

        private string ResolveCommandFailureStatus()
        {
            if (IsSignedIn)
            {
                return "Action required";
            }

            return IsSignInStepVisible ? "Sign-in failed" : "Action required";
        }
    }
}
