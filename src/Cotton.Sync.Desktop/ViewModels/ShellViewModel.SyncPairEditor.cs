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
        private async Task ToggleSelectedSyncPairEnabledAsync()
        {
            SyncPairRowViewModel? selected = SelectedSyncPair;
            if (selected is null)
            {
                return;
            }

            bool enabled = !selected.IsEnabled;
            bool wasSyncPaused = IsSyncPaused;
            IsBusy = true;
            try
            {
                await _controller.SetSyncPairEnabledAsync(selected.Id, enabled).ConfigureAwait(true);
                selected.IsEnabled = enabled;
                OnPropertyChanged(nameof(SelectedSyncPairToggleEnabledLabel));
                if (enabled)
                {
                    selected.Status = wasSyncPaused ? "Paused" : "Idle";
                }
                else
                {
                    selected.Status = "Disabled";
                }

                selected.CurrentOperation = string.Empty;
                if (wasSyncPaused && HasEnabledSyncPairs)
                {
                    GlobalStatus = "Paused";
                }
                else
                {
                    GlobalStatus = enabled ? "Ready" : "Folder disabled";
                }

                ActionRequiredMessage = string.Empty;
                AddActivity("Pair", selected.LocalPath, enabled ? "Folder enabled" : "Folder disabled");
                RefreshCurrentProgressText();
                RefreshDiagnosticsItems();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SaveSelectedSyncPairNameAsync()
        {
            SyncPairRowViewModel? selected = SelectedSyncPair;
            if (selected is null)
            {
                return;
            }

            string displayName = selected.EditableDisplayName.Trim();
            if (displayName.Length == 0)
            {
                GlobalStatus = "Action required";
                ActionRequiredMessage = "Sync folder name is required.";
                AddActivity("Warning", selected.LocalPath, "Sync folder name is required");
                return;
            }

            IsBusy = true;
            try
            {
                await _controller.RenameSyncPairAsync(selected.Id, displayName).ConfigureAwait(true);
                selected.DisplayName = displayName;
                selected.EditableDisplayName = displayName;
                OnPropertyChanged(nameof(SelectedSyncPairEditableDisplayName));
                RaiseTrayOpenFolderState();
                GlobalStatus = "Folder renamed";
                ActionRequiredMessage = string.Empty;
                AddActivity("Pair", selected.LocalPath, "Sync folder renamed to " + displayName);
                RefreshDiagnosticsItems();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private Task RequestRemoveSelectedSyncPairAsync()
        {
            SyncPairRowViewModel? selected = SelectedSyncPair;
            if (selected is not null)
            {
                IsSelectedSyncPairEditorVisible = true;
                SetPendingRemoveSyncPair(selected);
            }

            return Task.CompletedTask;
        }

        private Task CancelRemoveSyncPairAsync()
        {
            ClearRemoveSyncPairConfirmation();
            return Task.CompletedTask;
        }

        private Task ShowSelectedSyncPairEditorAsync(object? parameter)
        {
            SyncPairRowViewModel? target = ResolveSyncPairTarget(parameter);
            if (target is null)
            {
                return Task.CompletedTask;
            }

            if (ReferenceEquals(SelectedSyncPair, target) && IsSelectedSyncPairEditorVisible)
            {
                ClearRemoveSyncPairConfirmation();
                IsSelectedSyncPairEditorVisible = false;
                return Task.CompletedTask;
            }

            SelectedSyncPair = target;
            ClearRemoveSyncPairConfirmation();
            IsSelectedSyncPairEditorVisible = true;
            IsActivityVisible = false;
            return Task.CompletedTask;
        }

        private Task CancelSelectedSyncPairEditorAsync()
        {
            ClearRemoveSyncPairConfirmation();
            IsSelectedSyncPairEditorVisible = false;
            return Task.CompletedTask;
        }

        private async Task ConfirmRemoveSelectedSyncPairAsync()
        {
            SyncPairRowViewModel? selected = _pendingRemoveSyncPair;
            if (selected is null)
            {
                return;
            }

            IsBusy = true;
            IsRemovingSyncPair = true;
            GlobalStatus = "Removing sync folder";
            RefreshCurrentProgressText();
            try
            {
                await Task.Yield();
                await Task.Run(
                        async () => await _controller.RemoveSyncPairAsync(selected.Id).ConfigureAwait(false))
                    .ConfigureAwait(true);
                int removedIndex = SyncPairs.IndexOf(selected);
                SyncPairs.Remove(selected);
                ClearRemoveSyncPairConfirmation();
                IsSelectedSyncPairEditorVisible = false;
                SelectedSyncPair = SyncPairs.Count == 0
                    ? null
                    : SyncPairs[Math.Clamp(removedIndex, 0, SyncPairs.Count - 1)];
                GlobalStatus = SyncPairs.Count == 0 ? "Ready to add a folder" : "Ready";
                ActionRequiredMessage = string.Empty;
                AddActivity("Pair", selected.LocalPath, "Sync folder removed");
                RefreshDiagnosticsItems();
            }
            finally
            {
                IsRemovingSyncPair = false;
                IsBusy = false;
                RefreshCurrentProgressText();
            }
        }

        private void SetPendingRemoveSyncPair(SyncPairRowViewModel? syncPair)
        {
            if (ReferenceEquals(_pendingRemoveSyncPair, syncPair))
            {
                return;
            }

            _pendingRemoveSyncPair = syncPair;
            OnPropertyChanged(nameof(IsRemoveSyncPairConfirmationVisible));
            OnPropertyChanged(nameof(IsRemoveSyncPairConfirmationActionsVisible));
            OnPropertyChanged(nameof(RemoveSyncPairConfirmationTitle));
            OnPropertyChanged(nameof(RemoveSyncPairConfirmationMessage));
            OnPropertyChanged(nameof(RemoveSyncPairProgressMessage));
            RemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
            ConfirmRemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
            CancelRemoveSyncPairCommand.RaiseCanExecuteChanged();
        }

        private void ClearRemoveSyncPairConfirmation()
        {
            SetPendingRemoveSyncPair(null);
        }

        private void UpdateSelectedSyncPairEditorVisibility()
        {
            if (SelectedSyncPair is { } selected)
            {
                selected.IsEditorVisible = IsSelectedSyncPairEditorVisible;
            }
        }
    }
}
