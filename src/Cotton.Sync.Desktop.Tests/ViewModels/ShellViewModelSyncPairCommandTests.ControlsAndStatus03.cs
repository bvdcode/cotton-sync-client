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
        public async Task StatusChanged_BoundsChangingErrorFloodAndNativeNotifications()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: true,
                    CreatePair(syncPairId, "Documents", "Idle")));
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();

            for (int index = 1; index <= 100; index++)
            {
                controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(
                        syncPairId,
                        "Error",
                        "Local file 'Locked/report-" + index + ".docx' cannot be read because permission was denied."),
                ]));
            }

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(30));
                Assert.That(viewModel.Activities, Is.All.Matches<ActivityRowViewModel>(activity => activity.Kind == "Error"));
                Assert.That(viewModel.ActionRequiredMessage, Does.Contain("report-100.docx"));
                Assert.That(viewModel.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
            });

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null),
            ]));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Error",
                    "Local file 'Locked/after-recovery.docx' cannot be read because permission was denied."),
            ]));

            Assert.That(notificationService.Notifications, Has.Count.EqualTo(2));
        }
    }
}
