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
        private async Task OpenFolderAsync(object? parameter)
        {
            SyncPairRowViewModel? selected = ResolveOpenFolderTarget(parameter);
            if (selected is null)
            {
                return;
            }

            await _controller.OpenFolderAsync(selected.LocalPath).ConfigureAwait(true);
            AddActivity("Open", selected.LocalPath, "Folder opened");
        }

        private Task OpenTrayFolderAsync()
        {
            return SyncPairs.Count == 1
                ? OpenFolderAsync(SyncPairs[0])
                : Task.CompletedTask;
        }

        private SyncPairRowViewModel? ResolveOpenFolderTarget(object? parameter)
        {
            return parameter as SyncPairRowViewModel ?? SelectedSyncPair;
        }

        private SyncPairRowViewModel? ResolveSyncPairTarget(object? parameter)
        {
            return parameter as SyncPairRowViewModel ?? SelectedSyncPair;
        }

        private async Task OpenConflictAsync(object? parameter)
        {
            if (parameter is not ConflictRowViewModel conflict)
            {
                return;
            }

            SyncPairRowViewModel? syncPair = ResolveConflictSyncPair(conflict);
            if (syncPair is null)
            {
                GlobalStatus = "Action required";
                ActionRequiredMessage = "Sync folder for conflict was not found.";
                AddActivity("Warning", conflict.Path, "Sync folder for conflict was not found");
                return;
            }

            string openPath = ResolveConflictOpenPath(syncPair.LocalPath, conflict.Path);
            await _controller.OpenFolderAsync(openPath).ConfigureAwait(true);
            ActionRequiredMessage = string.Empty;
            AddActivity("Open", openPath, "Conflict location opened");
        }

        private async Task OpenWebAsync()
        {
            await _controller.OpenWebAsync().ConfigureAwait(true);
            AddActivity("Open", string.Empty, "Cotton Cloud opened");
        }

        private Task ToggleActivityAsync()
        {
            IsActivityVisible = !IsActivityVisible;
            return Task.CompletedTask;
        }
    }
}
