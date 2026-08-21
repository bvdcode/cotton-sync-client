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
        private void OnSyncPairsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasNoSyncPairs));
            OnPropertyChanged(nameof(HasSyncPairs));
            OnPropertyChanged(nameof(IsStatusCardVisible));
            OnPropertyChanged(nameof(HasDashboardNotifications));
            RaiseSyncStateProperties();
            OpenFolderCommand.RaiseCanExecuteChanged();
            RaiseTrayOpenFolderState();
            ToggleSelectedSyncPairEnabledCommand.RaiseCanExecuteChanged();
            SaveSelectedSyncPairNameCommand.RaiseCanExecuteChanged();
            RemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
            ShowSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
            RefreshCurrentProgressText();
            RefreshDiagnosticsItems();
        }

        private void OnActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasNoActivities));
            OnPropertyChanged(nameof(HasActivities));
        }

        private void OnConflictsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasStatusAttention));
            OnPropertyChanged(nameof(HasOfflineStatus));
            OnPropertyChanged(nameof(IsStatusCardVisible));
            OnPropertyChanged(nameof(HasDashboardNotifications));
            OnPropertyChanged(nameof(ConflictCountLabel));
            OnPropertyChanged(nameof(HeaderStatusText));
            OnPropertyChanged(nameof(StatusCardTitle));
            OnPropertyChanged(nameof(StatusCardDetailText));
            OnPropertyChanged(nameof(HasStatusCardDetail));
            RefreshCurrentProgressText();
        }

        private void OnRemoteFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RaiseRemoteFolderListStateProperties();
            OpenRemoteFolderCommand.RaiseCanExecuteChanged();
        }

        private void OnSelfTestItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasNoSelfTestItems));
            OnPropertyChanged(nameof(HasSelfTestItems));
        }

        private void OnNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasNotifications));
            OnPropertyChanged(nameof(HasDashboardNotifications));
        }
    }
}
