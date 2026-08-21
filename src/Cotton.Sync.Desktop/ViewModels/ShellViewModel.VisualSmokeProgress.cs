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
        private void ApplyVisualSmokeHydrationProgressScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = DateTime.UtcNow;
            const string relativePath = "Music/Albums/Album 001/track-0040.flac";
            const int totalFiles = 2000;
            const long totalBytes = 8_388_608_000;
            const long currentFileBytes = 3_145_728;
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.HydratingCloudFiles,
                FilesCompleted: 0,
                totalFiles,
                relativePath,
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc,
                BytesCompleted: 0,
                totalBytes));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Download,
                relativePath,
                TransferredBytes: 0,
                currentFileBytes,
                IsCompleted: false,
                startedAtUtc));
            AddActivity("Download", relativePath, "Downloading " + Path.GetFileName(relativePath));
        }

        private void ApplyVisualSmokeDehydrationProgressScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = DateTime.UtcNow;
            const string relativePath = "Music/Albums/Album 001/track-0020.flac";
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.DehydratingCloudFiles,
                FilesCompleted: 0,
                FilesTotal: 1000,
                relativePath,
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc));
        }

        private void ApplyVisualSmokeHighPressureStartingScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = new(2026, 6, 15, 11, 20, 0, DateTimeKind.Utc);
            const int totalFiles = 1494;
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: totalFiles,
                CurrentPath: string.Empty,
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(3)));
            AddActivity("Sync", syncPair.RemotePath, "Processing queued file changes");
        }

        private void ApplyVisualSmokeVirtualFilesSeedingScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = new(2026, 6, 24, 3, 40, 0, DateTimeKind.Utc);
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 118_054,
                FilesTotal: 500_000,
                CurrentPath: "Photos/2026/image-118054.heic",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(2)));
            AddActivity("Sync", syncPair.RemotePath, "Making cloud files available");
        }

        private void ApplyVisualSmokeUpdateDownloadProgressScenario()
        {
            DesktopUpdateDownloadProgress progress = new DesktopUpdateDownloadProgress(
                "0.1.49",
                "CottonSync-Windows-Setup.exe",
                25_165_824,
                100_663_296);
            IsUpdateAvailable = true;
            IsUpdateReady = false;
            IsUpdateBusy = true;
            IsUpdateInstallHandoffActive = false;
            UpdateStatusText = "Downloading update";
            UpdateDetailsText = FormatUpdateDownloadProgress(progress);
            GlobalStatus = "Downloading update";
            IsUpdateDownloadProgressVisible = true;
            IsUpdateDownloadProgressIndeterminate = false;
            UpdateDownloadProgressValue = 25d;
            IsUpdateInstallProgressVisible = false;
        }

        private void ApplyVisualSmokeUpdateInstallProgressScenario()
        {
            IsUpdateAvailable = true;
            IsUpdateReady = true;
            IsUpdateBusy = true;
            IsUpdateInstallHandoffActive = false;
            UpdateStatusText = "Installing update";
            UpdateDetailsText = "Starting the update installer.";
            GlobalStatus = "Installing update";
            ClearUpdateDownloadProgress();
            IsUpdateInstallProgressVisible = true;
        }
    }
}
