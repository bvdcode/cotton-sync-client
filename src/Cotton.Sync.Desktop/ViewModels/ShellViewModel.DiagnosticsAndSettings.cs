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
        private async Task ExportDiagnosticsAsync()
        {
            bool preserveActionRequired = HasActionRequired;
            string previousGlobalStatus = GlobalStatus;
            string previousActionRequiredMessage = ActionRequiredMessage;
            IsExportingDiagnostics = true;
            GlobalStatus = "Exporting diagnostics";
            long statusPresentationRevision = _statusPresentationRevision;
            try
            {
                await YieldToUiDispatcherAsync().ConfigureAwait(true);
                string bundlePath = await _controller.ExportDiagnosticsAsync().ConfigureAwait(true);
                LastDiagnosticsBundlePath = bundlePath;
                if (_statusPresentationRevision == statusPresentationRevision && preserveActionRequired)
                {
                    GlobalStatus = string.IsNullOrWhiteSpace(previousGlobalStatus)
                        ? "Action required"
                        : previousGlobalStatus;
                    ActionRequiredMessage = previousActionRequiredMessage;
                }
                else if (_statusPresentationRevision == statusPresentationRevision)
                {
                    GlobalStatus = "Diagnostics exported";
                    ActionRequiredMessage = string.Empty;
                }

                AddActivity("Diagnostics", bundlePath, "Diagnostics bundle exported to " + bundlePath);
            }
            finally
            {
                IsExportingDiagnostics = false;
            }
        }

        private Task YieldToUiDispatcherAsync()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiDispatcher.Post(() => completion.TrySetResult());
            return completion.Task;
        }

        private async Task OpenDataFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(DataDirectory))
            {
                return;
            }

            await _controller.OpenFolderAsync(DataDirectory).ConfigureAwait(true);
            AddActivity("Open", DataDirectory, "Data folder opened");
        }

        private async Task OpenDiagnosticsBundleFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(LastDiagnosticsBundlePath))
            {
                return;
            }

            string? directory = Path.GetDirectoryName(LastDiagnosticsBundlePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            await _controller.OpenFolderAsync(directory).ConfigureAwait(true);
            AddActivity("Open", directory, "Diagnostics folder opened");
        }

        private Task ShowSettingsAsync()
        {
            IsSettingsVisible = true;
            return Task.CompletedTask;
        }

        private Task CloseSettingsAsync()
        {
            IsSettingsVisible = false;
            return Task.CompletedTask;
        }
    }
}
