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
        internal async Task ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario? scenario)
        {
            if (scenario is null)
            {
                return;
            }

            bool isApplied = await TryApplyVisualSmokeSetupScenarioAsync(scenario.Value).ConfigureAwait(true)
                || TryApplyVisualSmokeProgressScenario(scenario.Value)
                || await TryApplyVisualSmokeStateScenarioAsync(scenario.Value).ConfigureAwait(true);
            if (!isApplied)
            {
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            RefreshCurrentProgressText();
            RefreshDiagnosticsItems();
            RaiseCommandStates();
        }

        private async Task<bool> TryApplyVisualSmokeSetupScenarioAsync(DesktopVisualSmokeScenario scenario)
        {
            switch (scenario)
            {
                case DesktopVisualSmokeScenario.Connecting:
                    SetSnapshotLoading(true);
                    return true;
                case DesktopVisualSmokeScenario.SignInError:
                    ServerUrl = "https://app.cottoncloud.dev/";
                    IsServerProbeChecking = false;
                    IsServerProbeFailed = false;
                    IsServerVerified = true;
                    ServerProbeStatus = "Cotton Cloud verified";
                    Username = string.IsNullOrWhiteSpace(Username) ? "qa@cottoncloud.dev" : Username;
                    Password = "wrong-password";
                    TotpCode = string.Empty;
                    GlobalStatus = "Sign-in failed";
                    ActionRequiredMessage = "Invalid username or password.";
                    return true;
                case DesktopVisualSmokeScenario.AddFolder:
                case DesktopVisualSmokeScenario.AddFolderManyRemoteFolders:
                    LocalFolderPath = CreateVisualSmokeLocalRootPath();
                    IsAddSyncPairWizardVisible = true;
                    await LoadRemoteFoldersAsync("/").ConfigureAwait(true);
                    return true;
                case DesktopVisualSmokeScenario.EmptyDashboard:
                case DesktopVisualSmokeScenario.Dashboard:
                    return true;
                case DesktopVisualSmokeScenario.Settings:
                    SelectedSettingsTabIndex = 0;
                    await ShowSettingsAsync().ConfigureAwait(true);
                    return true;
                case DesktopVisualSmokeScenario.SettingsDiagnostics:
                    SelectedSettingsTabIndex = 2;
                    await ShowSettingsAsync().ConfigureAwait(true);
                    await SelfTestAsync().ConfigureAwait(true);
                    await ExportDiagnosticsAsync().ConfigureAwait(true);
                    return true;
                default:
                    return false;
            }
        }

        private bool TryApplyVisualSmokeProgressScenario(DesktopVisualSmokeScenario scenario)
        {
            switch (scenario)
            {
                case DesktopVisualSmokeScenario.Progress:
                    ApplyVisualSmokeProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.LongProgress:
                    ApplyVisualSmokeLongProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.ManySmallDownload:
                    ApplyVisualSmokeManySmallDownloadScenario();
                    return true;
                case DesktopVisualSmokeScenario.HydrationProgress:
                    ApplyVisualSmokeHydrationProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.DehydrationProgress:
                    ApplyVisualSmokeDehydrationProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.HighPressureStarting:
                    ApplyVisualSmokeHighPressureStartingScenario();
                    return true;
                case DesktopVisualSmokeScenario.VirtualFilesSeeding:
                    ApplyVisualSmokeVirtualFilesSeedingScenario();
                    return true;
                default:
                    return false;
            }
        }

        private async Task<bool> TryApplyVisualSmokeStateScenarioAsync(DesktopVisualSmokeScenario scenario)
        {
            switch (scenario)
            {
                case DesktopVisualSmokeScenario.Error:
                    GlobalStatus = "Action required";
                    ActionRequiredMessage = DesktopActionRequiredMessageResolver.MissingDesktopSyncChangesApiMessage;
                    AddActivity("Error", SelectedSyncPair?.LocalPath ?? string.Empty, ActionRequiredMessage);
                    return true;
                case DesktopVisualSmokeScenario.Offline:
                    ApplyVisualSmokeOfflineScenario();
                    return true;
                case DesktopVisualSmokeScenario.MissingLocalRoot:
                    ApplyVisualSmokeMissingLocalRootScenario();
                    return true;
                case DesktopVisualSmokeScenario.UpdateDownloadProgress:
                    SelectedSettingsTabIndex = 0;
                    await ShowSettingsAsync().ConfigureAwait(true);
                    ApplyVisualSmokeUpdateDownloadProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.UpdateInstallProgress:
                    SelectedSettingsTabIndex = 0;
                    await ShowSettingsAsync().ConfigureAwait(true);
                    ApplyVisualSmokeUpdateInstallProgressScenario();
                    return true;
                case DesktopVisualSmokeScenario.FolderControls:
                    if (SyncPairs.FirstOrDefault() is { } syncPair)
                    {
                        await ShowSelectedSyncPairEditorAsync(syncPair).ConfigureAwait(true);
                    }

                    return true;
                case DesktopVisualSmokeScenario.Conflict:
                    AddActivity("Conflict", "Reports/budget.xlsx", "Local and cloud versions changed at the same time.");
                    AddConflict(
                        SelectedSyncPair?.Id,
                        "Reports/budget.xlsx",
                        "Local and cloud versions changed at the same time.",
                        DateTimeOffset.Now);
                    return true;
                default:
                    return false;
            }
        }

        private static string CreateVisualSmokeLocalRootPath()
        {
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Cotton")
                : "/home/qa/Cotton";
        }

        private void ApplyVisualSmokeMissingLocalRootScenario()
        {
            const string message =
                "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.";
            GlobalStatus = "Action required";
            ActionRequiredMessage = message;
            if (SelectedSyncPair is { } syncPair)
            {
                syncPair.Status = "Error";
                syncPair.LastError = message;
                AddActivity("Error", syncPair.LocalPath, message);
            }
            else
            {
                AddActivity("Error", string.Empty, message);
            }
        }

        private void ApplyVisualSmokeOfflineScenario()
        {
            const string message = "Cannot reach Cotton Cloud. Sync will retry automatically.";
            GlobalStatus = "Offline";
            SyncPairRowViewModel? activityPair = null;
            foreach (SyncPairRowViewModel syncPair in SyncPairs.Where(static pair => pair.IsEnabled))
            {
                syncPair.Status = "Offline";
                syncPair.LastError = message;
                activityPair ??= syncPair;
            }

            if (activityPair is not null)
            {
                AddActivity("Network", activityPair.LocalPath, message);
            }
            else
            {
                AddActivity("Network", string.Empty, message);
            }

            RaiseSyncStateProperties();
        }

        private void ApplyVisualSmokeProgressScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            SyncPairRowViewModel? secondSyncPair = SyncPairs.Skip(1).FirstOrDefault();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 15, 0, DateTimeKind.Utc);
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 8,
                FilesTotal: 31,
                CurrentPath: "Reports/quarterly-budget.xlsx",
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc.AddSeconds(6)));
            if (secondSyncPair is not null)
            {
                secondSyncPair.Status = "Syncing";
                ApplyRunProgress(new DesktopRunProgressSnapshot(
                    secondSyncPair.Id,
                    SyncRunProgressStage.ReconcilingFiles,
                    FilesCompleted: 2,
                    FilesTotal: 9,
                    CurrentPath: "Blink/2024/07.7z",
                    startedAtUtc,
                    IsCompleted: false,
                    startedAtUtc.AddSeconds(6)));
            }

            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Upload,
                "Reports/quarterly-budget.xlsx",
                TransferredBytes: 0,
                TotalBytes: 25_165_824,
                IsCompleted: false,
                startedAtUtc));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Upload,
                "Reports/quarterly-budget.xlsx",
                TransferredBytes: 6_291_456,
                TotalBytes: 25_165_824,
                IsCompleted: false,
                startedAtUtc.AddSeconds(2),
                SpeedBytesPerSecond: 3_145_728,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(6)));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Upload,
                "Reports/forecast.xlsx",
                TransferredBytes: 3_145_728,
                TotalBytes: 6_291_456,
                IsCompleted: false,
                startedAtUtc.AddSeconds(2),
                SpeedBytesPerSecond: 1_048_576,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(3)));
            if (secondSyncPair is not null)
            {
                ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                    secondSyncPair.Id,
                    SyncTransferDirection.Download,
                    "Blink/2024/07.7z",
                    TransferredBytes: 1_048_576,
                    TotalBytes: 3_145_728,
                    IsCompleted: false,
                    startedAtUtc.AddSeconds(2),
                    SpeedBytesPerSecond: 1_048_576,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));
            }

            AddActivity("Upload", "Reports/quarterly-budget.xlsx", "Uploading quarterly-budget.xlsx");
        }

        private void ApplyVisualSmokeLongProgressScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = new(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc);
            const string longPath =
                "Reports/Finance/quarterly-budget-with-a-very-long-file-name-that-should-stay-ellipsized-in-active-progress-final-approved-upload-copy-2026-06-15.xlsx";
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 17,
                FilesTotal: 42,
                CurrentPath: longPath,
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc.AddSeconds(8)));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Upload,
                longPath,
                TransferredBytes: 9_437_184,
                TotalBytes: 37_748_736,
                IsCompleted: false,
                startedAtUtc.AddSeconds(8),
                SpeedBytesPerSecond: 2_359_296,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(12)));
            AddActivity("Upload", longPath, "Uploading " + Path.GetFileName(longPath));
        }

        private void ApplyVisualSmokeManySmallDownloadScenario()
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault();
            if (syncPair is null)
            {
                return;
            }

            DateTime startedAtUtc = new(2026, 6, 15, 11, 5, 0, DateTimeKind.Utc);
            const string relativePath = "Downloads/small-files/batch-0410.txt";
            const int completedFiles = 410;
            const int totalFiles = 500;
            const long fileSize = 4096;
            GlobalStatus = "Syncing";
            syncPair.Status = "Syncing";
            ApplyRunProgress(new DesktopRunProgressSnapshot(
                syncPair.Id,
                SyncRunProgressStage.ReconcilingFiles,
                completedFiles,
                totalFiles,
                relativePath,
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc.AddSeconds(24),
                BytesCompleted: completedFiles * fileSize,
                BytesTotal: totalFiles * fileSize));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Download,
                relativePath,
                TransferredBytes: fileSize * 3 / 4,
                TotalBytes: fileSize,
                IsCompleted: false,
                startedAtUtc.AddSeconds(24),
                SpeedBytesPerSecond: fileSize * 2,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(1)));
            ApplyTransferProgress(new DesktopTransferProgressSnapshot(
                syncPair.Id,
                SyncTransferDirection.Download,
                "Downloads/small-files/batch-0411.txt",
                TransferredBytes: fileSize / 2,
                TotalBytes: fileSize,
                IsCompleted: false,
                startedAtUtc.AddSeconds(24),
                SpeedBytesPerSecond: fileSize,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(1)));
            AddActivity("Download", relativePath, "Downloading " + Path.GetFileName(relativePath));
        }
    }
}
