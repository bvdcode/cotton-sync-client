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
        private async Task AddSyncPairAsync()
        {
            IsAddingSyncPair = true;
            GlobalStatus = "Adding sync folder";
            RefreshCurrentProgressText();
            try
            {
                SyncPairSettings syncPair = await _controller.AddSyncPairAsync(
                    new DesktopSyncPairRequest(LocalFolderPath, RemoteFolderPath, SelectedSyncMode)).ConfigureAwait(true);
                SyncPairRowViewModel row = ToRow(syncPair);
                SyncPairs.Add(row);
                SelectedSyncPair = row;
                LocalFolderPath = string.Empty;
                RemoteFolderPath = string.Empty;
                SelectedSyncMode = SyncPairMode.FullMirror;
                IsAddSyncPairWizardVisible = false;
                ActionRequiredMessage = string.Empty;
                RemoteFolders.Clear();
                IsSelectedSyncPairEditorVisible = false;
                GlobalStatus = "Sync requested";
                RefreshCurrentProgressText();
                AddActivity("Pair", syncPair.LocalRootPath, "Folder added and initial sync requested");
                RefreshDiagnosticsItems();
                RaiseCommandStates();
            }
            finally
            {
                IsAddingSyncPair = false;
            }
        }

        private async Task BrowseLocalFolderAsync()
        {
            string? selectedPath = await _folderPicker.PickFolderAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            string? overlapMessage = GetLocalFolderOverlapMessage(selectedPath);
            if (overlapMessage is not null)
            {
                LocalFolderPath = string.Empty;
                NewRemoteFolderName = string.Empty;
                IsCreateRemoteFolderVisible = false;
                ResetRemoteFolderSelection();
                RemoteFolders.Clear();
                _isLocalFolderSelectionError = true;
                GlobalStatus = "Action required";
                ActionRequiredMessage = overlapMessage;
                AddActivity("Warning", selectedPath, ActionRequiredMessage);
                RefreshCurrentProgressText();
                return;
            }

            LocalFolderPath = selectedPath;
            ClearLocalFolderSelectionError();
            AddActivity("Folder", selectedPath, "Local folder selected");
            if (IsAddSyncPairWizardVisible)
            {
                NewRemoteFolderName = string.Empty;
                IsCreateRemoteFolderVisible = false;
                await LoadRemoteFoldersAsync("/").ConfigureAwait(true);
            }
        }

        private Task CancelAddSyncPairAsync()
        {
            LocalFolderPath = string.Empty;
            SelectedSyncMode = SyncPairMode.FullMirror;
            NewRemoteFolderName = string.Empty;
            IsCreateRemoteFolderVisible = false;
            ResetRemoteFolderSelection();
            ClearLocalFolderSelectionError();
            IsAddSyncPairWizardVisible = false;
            return Task.CompletedTask;
        }

        private Task ChangeServerAsync()
        {
            Password = string.Empty;
            TotpCode = string.Empty;
            SetDesktopSyncChangesApiUnavailable(false);
            IsServerVerified = false;
            IsServerProbeFailed = false;
            ServerProbeStatus = "Edit server address";
            return Task.CompletedTask;
        }

        private bool CanGoUpRemoteFolder()
        {
            return !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow
                && IsAddSyncPairWizardVisible
                && RemoteBrowserPath != "/";
        }

        private Task UseRemoteFolderAsync()
        {
            return AddSyncPairAsync();
        }
    }
}
