// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {

        private static async Task ExecuteAsync(AsyncRelayCommand command, object? parameter = null)
        {
            Assert.That(command.CanExecute(parameter), Is.True);
            command.Execute(parameter);
            for (int attempt = 0; attempt < 50 && command.IsRunning; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.That(command.IsRunning, Is.False);
        }


        private static async Task WaitForAsync(Func<bool> condition, int attempts = 100)
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail("Condition was not met before timeout.");
        }


        private static DesktopShellSnapshot CreateSignedOutSnapshot(bool enableNotifications = true)
        {
            return new DesktopShellSnapshot(
                null,
                null,
                null,
                false,
                enableNotifications,
                AppThemeMode.System,
                CreateTestDataPathSnapshot(),
                CreatePlatformCapabilities(),
                false,
                []);
        }


        private static DesktopShellSnapshot CreateStoredSessionWaitingSnapshot()
        {
            return new DesktopShellSnapshot(
                new Uri("https://cotton.example.test/"),
                null,
                "restored@example.test",
                false,
                true,
                AppThemeMode.System,
                CreateTestDataPathSnapshot(),
                CreatePlatformCapabilities(),
                false,
                [],
                StartupErrorMessage:
                    "Cotton Cloud reports that the server is locked. Unlock it in the web app; Cotton Sync will retry automatically.",
                HasStoredSession: true);
        }


        private static DesktopShellSnapshot CreateSignedInSnapshot(
            params DesktopSyncPairSnapshot[] syncPairs)
        {
            return CreateSignedInSnapshotWithNotifications(enableNotifications: true, syncPairs);
        }


        private static DesktopShellSnapshot CreateSignedInSnapshot(
            DesktopPlatformCapabilitySnapshot platformCapabilities,
            params DesktopSyncPairSnapshot[] syncPairs)
        {
            return CreateSignedInSnapshotWithNotificationsAndCapabilities(
                enableNotifications: true,
                platformCapabilities,
                syncPairs);
        }


        private static DesktopShellSnapshot CreateSignedInSnapshotWithNotifications(
            bool enableNotifications,
            params DesktopSyncPairSnapshot[] syncPairs)
        {
            return CreateSignedInSnapshotWithNotificationsAndCapabilities(
                enableNotifications,
                CreatePlatformCapabilities(),
                syncPairs);
        }


        private static DesktopShellSnapshot CreateSignedInSnapshotWithNotificationsAndCapabilities(
            bool enableNotifications,
            DesktopPlatformCapabilitySnapshot platformCapabilities,
            params DesktopSyncPairSnapshot[] syncPairs)
        {
            return new DesktopShellSnapshot(
                null,
                "vadim@example.com",
                "vadim@example.com",
                false,
                enableNotifications,
                AppThemeMode.System,
                CreateTestDataPathSnapshot(),
                platformCapabilities,
                true,
                syncPairs);
        }


        private static DesktopPlatformCapabilitySnapshot CreatePlatformCapabilities(
            bool windowsVirtualFilesSupported = false)
        {
            return new DesktopPlatformCapabilitySnapshot(
                windowsVirtualFilesSupported ? "Windows" : "Linux",
                "test",
                "test",
                true,
                windowsVirtualFilesSupported,
                windowsVirtualFilesSupported
                    ? "Supported on Windows through the native tray lifecycle."
                    : "Tray lifecycle is not supported in this test.",
                windowsVirtualFilesSupported,
                windowsVirtualFilesSupported
                    ? "Windows Cloud Files API is available."
                    : "Windows virtual files require the Windows Cloud Files API.");
        }


        private static DesktopDataPathSnapshot CreateTestDataPathSnapshot()
        {
            string dataDirectory = Path.Combine(Path.GetTempPath(), "cotton-sync-test-data");
            return new DesktopDataPathSnapshot(
                dataDirectory,
                Path.Combine(dataDirectory, "sync-app.db"),
                Path.Combine(dataDirectory, "sync-state.db"),
                Path.Combine(dataDirectory, "tokens.json"));
        }


        private static DesktopSyncPairSnapshot CreatePair(
            Guid id,
            string displayName,
            string status,
            DateTime? lastSyncedAtUtc = null,
            string? localPath = null,
            string? remotePath = null,
            SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new DesktopSyncPairSnapshot(
                id,
                displayName,
                localPath ?? "/home/vadim/" + displayName,
                remotePath ?? "/" + displayName,
                status,
                Guid.NewGuid(),
                lastSyncedAtUtc,
                Mode: mode);
        }


        private static FakeDesktopShellController CreateTwoFolderSyncingController(Guid documentsPairId, Guid videosPairId)
        {
            return new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(documentsPairId, "Documents", "Syncing"),
                    CreatePair(videosPairId, "Videos", "Syncing")));
        }


        private static void ReportTwoFolderCheckingProgress(
            FakeDesktopShellController controller,
            Guid documentsPairId,
            Guid videosPairId)
        {
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                documentsPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 10,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 20,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                documentsPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 5,
                FilesTotal: 20,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 6, DateTimeKind.Utc)));
        }
    }
}
