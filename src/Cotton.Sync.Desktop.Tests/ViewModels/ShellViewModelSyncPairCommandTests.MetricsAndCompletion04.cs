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

        [Test]
        public async Task StatusChanged_RecordsCompletionNotificationWithoutDashboardCard()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null),
            ]));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNotifications, Is.True);
                Assert.That(viewModel.Notifications.Single().Title, Is.EqualTo("Initial sync complete"));
                Assert.That(viewModel.HasDashboardNotifications, Is.False);
            });

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Paused", null),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNotifications, Is.True);
                Assert.That(viewModel.HasDashboardNotifications, Is.False);
                Assert.That(viewModel.IsStatusCardVisible, Is.True);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
            });
        }


        [Test]
        public async Task StatusChanged_DelaysVirtualFilesCompletionNotificationWhileCloudFilesProgressIsFresh()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null),
            ]));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 3,
                FilesTotal: 100,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 8, 0, 1, DateTimeKind.Utc)));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 0, DateTimeKind.Utc)),
            ]));

            Assert.That(viewModel.HasNotifications, Is.False);
            Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Syncing"));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 1, DateTimeKind.Utc)),
            ]));

            Assert.That(viewModel.HasNotifications, Is.False);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 100,
                FilesTotal: 100,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc),
                IsCompleted: true,
                OccurredAtUtc: new DateTime(2026, 6, 4, 8, 1, 2, DateTimeKind.Utc)));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 3, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.HasNotifications, Is.True);
                Assert.That(viewModel.Notifications.Single().Title, Is.EqualTo("Initial sync complete"));
                Assert.That(viewModel.Notifications.Single().Message, Is.EqualTo("Cloud is up to date."));
            });
        }


        [Test]
        public async Task StatusChanged_DelaysVirtualFilesCompletionNotificationWhileCloudFilesStatusIsFinalizing()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null),
            ]));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                FilesCompleted: 1,
                FilesTotal: 10,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 8, 0, 1, DateTimeKind.Utc)));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 0, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNotifications, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Syncing"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Finalizing cloud file status · 1 of 10 folders"));
            });

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                FilesCompleted: 10,
                FilesTotal: 10,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc),
                IsCompleted: true,
                OccurredAtUtc: new DateTime(2026, 6, 4, 8, 1, 2, DateTimeKind.Utc)));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 3, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.HasNotifications, Is.True);
                Assert.That(viewModel.Notifications.Single().Title, Is.EqualTo("Initial sync complete"));
                Assert.That(viewModel.Notifications.Single().Message, Is.EqualTo("Cloud is up to date."));
            });
        }


        [Test]
        public async Task RunProgressChanged_HidesCompletionNotificationWhileAnotherFolderIsActive()
        {
            Guid completedPairId = Guid.NewGuid();
            Guid activePairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(completedPairId, "Documents", "Syncing"),
                    CreatePair(activePairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(completedPairId, "Syncing", null),
                new DesktopSyncPairStatusSnapshot(activePairId, "Syncing", null),
            ]));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                activePairId,
                SyncRunProgressStage.ScanningLocal,
                FilesCompleted: 0,
                FilesTotal: null,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 8, 0, 1, DateTimeKind.Utc)));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    completedPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 1, 0, DateTimeKind.Utc)),
                new DesktopSyncPairStatusSnapshot(activePairId, "Syncing", null),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNotifications, Is.True);
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.HasDashboardNotifications, Is.False);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Videos"));
            });
        }


        [Test]
        public async Task Initialize_AsksToEnableFolderWhenAllPairsAreDisabled()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Disabled")));
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStatusCardVisible, Is.True);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Enable a folder to start syncing."));
            });
        }
    }
}
