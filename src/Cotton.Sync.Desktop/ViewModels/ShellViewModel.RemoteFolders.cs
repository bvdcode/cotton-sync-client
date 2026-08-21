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
        private async Task OpenRemoteFolderAsync()
        {
            RemoteFolderRowViewModel? selected = SelectedRemoteFolder;
            if (selected is null)
            {
                return;
            }

            await LoadRemoteFoldersAsync(selected.Path).ConfigureAwait(true);
        }

        private async Task RemoteFolderUpAsync()
        {
            await LoadRemoteFoldersAsync(GetRemoteParentPath(RemoteBrowserPath)).ConfigureAwait(true);
        }

        private async Task ShowAddSyncPairAsync()
        {
            SelectedSyncMode = SyncPairMode.FullMirror;
            IsAddSyncPairWizardVisible = true;
            NewRemoteFolderName = string.Empty;
            IsCreateRemoteFolderVisible = false;

            if (HasLocalFolderSelection && string.IsNullOrWhiteSpace(RemoteFolderPath))
            {
                await LoadRemoteFoldersAsync("/").ConfigureAwait(true);
            }
        }

        private Task ShowCreateRemoteFolderAsync()
        {
            IsCreateRemoteFolderVisible = true;
            NewRemoteFolderName = string.Empty;
            return Task.CompletedTask;
        }

        private Task CancelCreateRemoteFolderAsync()
        {
            NewRemoteFolderName = string.Empty;
            IsCreateRemoteFolderVisible = false;
            return Task.CompletedTask;
        }

        private async Task CreateRemoteFolderAsync()
        {
            string folderName = NewRemoteFolderName.Trim();
            if (folderName.Length == 0)
            {
                GlobalStatus = "Action required";
                ActionRequiredMessage = "Cloud folder name is required.";
                AddActivity("Warning", RemoteBrowserPath, "Cloud folder name is required");
                return;
            }

            IsBusy = true;
            try
            {
                DesktopRemoteFolderSnapshot folder = await _controller
                    .CreateRemoteFolderAsync(RemoteBrowserPath, folderName)
                    .ConfigureAwait(true);
                NewRemoteFolderName = string.Empty;
                IsCreateRemoteFolderVisible = false;
                await LoadRemoteFoldersAsync(folder.Path).ConfigureAwait(true);
                GlobalStatus = "Cloud folder created";
                ActionRequiredMessage = string.Empty;
                AddActivity("Cloud", folder.Path, "Cloud folder created");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanAddSyncPair()
        {
            return !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow
                && IsSignedIn
                && !string.IsNullOrWhiteSpace(LocalFolderPath)
                && !string.IsNullOrWhiteSpace(RemoteFolderPath);
        }

        private bool CanBrowseLocalFolder()
        {
            return !IsBusy && !IsAddingSyncPair && CanUseAddSyncPairFlow;
        }

        private bool CanUseRemoteFolder()
        {
            return CanAddSyncPair();
        }

        private bool CanOpenRemoteFolder()
        {
            return !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow
                && SelectedRemoteFolder is not null;
        }

        private bool CanShowAddSyncPair()
        {
            return IsSignedIn
                && !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow;
        }

        private bool CanShowCreateRemoteFolder()
        {
            return !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow
                && IsAddSyncPairCloudStepVisible;
        }

        private string? GetLocalFolderOverlapMessage(string localPath, Guid? existingSyncPairId = null)
        {
            if (SyncPairs.Count == 0 && !existingSyncPairId.HasValue)
            {
                return null;
            }

            Guid candidateId = existingSyncPairId ?? Guid.NewGuid();
            List<SyncPairSettings> syncPairs = SyncPairs
                .Where(pair => pair.Id != candidateId)
                .Select(ToSettingsForValidation)
                .Append(new SyncPairSettings
                {
                    Id = candidateId,
                    DisplayName = "Candidate",
                    LocalRootPath = localPath,
                    RemoteRootNodeId = Guid.NewGuid(),
                    RemoteDisplayPath = "/",
                    IsEnabled = true,
                    Mode = SyncPairMode.FullMirror,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                })
                .ToList();
            return _syncPairSettingsValidator
                .Validate(syncPairs)
                .Errors
                .FirstOrDefault(error => error.Issue == SyncPairValidationIssue.OverlappingLocalRoots
                    && (error.SyncPairId == candidateId || error.OtherSyncPairId == candidateId))
                ?.Message;
        }

        private void ClearLocalFolderSelectionError()
        {
            if (!_isLocalFolderSelectionError)
            {
                return;
            }

            _isLocalFolderSelectionError = false;
            ActionRequiredMessage = string.Empty;
            if (IsSignedIn)
            {
                GlobalStatus = "Connected";
            }
        }

        private bool CanCreateRemoteFolder()
        {
            return !IsBusy
                && !IsAddingSyncPair
                && CanUseAddSyncPairFlow
                && IsSignedIn
                && IsAddSyncPairCloudStepVisible
                && !string.IsNullOrWhiteSpace(NewRemoteFolderName);
        }

        private bool CanUseAddSyncPairFlow => !_isDesktopSyncChangesApiUnavailable;

        private bool CanSignIn()
        {
            return !IsBusy
                && !string.IsNullOrWhiteSpace(ServerUrl)
                && !string.IsNullOrWhiteSpace(Username)
                && !string.IsNullOrEmpty(Password)
                && IsServerVerified;
        }

        private bool CanSignInWithBrowser()
        {
            return !IsBusy
                && !string.IsNullOrWhiteSpace(ServerUrl)
                && IsServerVerified;
        }

        private bool CanCancelBrowserSignIn()
        {
            return IsBrowserSignInPending && _browserSignInCancellation is not null;
        }

        private async Task LoadRemoteFoldersAsync(string remotePath)
        {
            IsBusy = true;
            IsRemoteFolderLoading = true;
            try
            {
                NewRemoteFolderName = string.Empty;
                IsCreateRemoteFolderVisible = false;
                DesktopRemoteFolderListSnapshot folders = await _controller
                    .ListRemoteFoldersAsync(remotePath)
                    .ConfigureAwait(true);
                RemoteBrowserPath = folders.CurrentPath;
                RemoteFolderPath = folders.CurrentPath;
                ClearRemoteFolderFilter();
                _remoteFolderRows.Clear();
                foreach (DesktopRemoteFolderSnapshot folder in folders.Folders)
                {
                    _remoteFolderRows.Add(new RemoteFolderRowViewModel
                    {
                        Id = folder.Id,
                        Name = folder.Name,
                        Path = folder.Path,
                    });
                }

                ApplyRemoteFolderFilter();
                SelectedRemoteFolder = null;
            }
            finally
            {
                IsRemoteFolderLoading = false;
                IsBusy = false;
            }
        }

        private void ResetRemoteFolderSelection()
        {
            RemoteBrowserPath = "/";
            RemoteFolderPath = string.Empty;
            SelectedRemoteFolder = null;
            NewRemoteFolderName = string.Empty;
            IsCreateRemoteFolderVisible = false;
            ClearRemoteFolderFilter();
            _remoteFolderRows.Clear();
            RemoteFolders.Clear();
            RaiseRemoteFolderListStateProperties();
        }

        private void ClearRemoteFolderFilter()
        {
            if (!string.IsNullOrEmpty(_remoteFolderFilter))
            {
                _remoteFolderFilter = string.Empty;
                OnPropertyChanged(nameof(RemoteFolderFilter));
            }
        }

        private void ApplyRemoteFolderFilter()
        {
            string filter = RemoteFolderFilter.Trim();
            RemoteFolders.Clear();
            IEnumerable<RemoteFolderRowViewModel> rows = _remoteFolderRows;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                rows = rows.Where(row => RemoteFolderMatchesFilter(row, filter));
            }

            foreach (RemoteFolderRowViewModel row in rows)
            {
                RemoteFolders.Add(row);
            }

            if (SelectedRemoteFolder is not null && !RemoteFolders.Contains(SelectedRemoteFolder))
            {
                SelectedRemoteFolder = null;
            }
        }

        private static bool RemoteFolderMatchesFilter(RemoteFolderRowViewModel row, string filter)
        {
            return row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || row.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }

        private void RaiseRemoteFolderListStateProperties()
        {
            OnPropertyChanged(nameof(HasNoRemoteFolders));
            OnPropertyChanged(nameof(HasRemoteFolders));
            OnPropertyChanged(nameof(RemoteFolderCountLabel));
            OnPropertyChanged(nameof(HasRemoteFolderCount));
            OnPropertyChanged(nameof(RemoteFolderEmptyTitle));
            OnPropertyChanged(nameof(RemoteFolderEmptySubtitle));
        }
    }
}
