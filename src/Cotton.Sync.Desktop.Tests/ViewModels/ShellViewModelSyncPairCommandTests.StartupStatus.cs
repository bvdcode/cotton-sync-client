// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public async Task Initialize_InitialSyncToastDependsOnPersistedCompletion(
            bool hasCompletedFullReconcile,
            bool hasPersistedFileTimestamp)
        {
            Guid pairId = Guid.NewGuid();
            DateTime completedAt = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            DesktopSyncPairSnapshot pair = CreatePair(pairId, "Notes", "Idle",
                lastSyncedAtUtc: hasPersistedFileTimestamp ? completedAt : null) with
            {
                HasCompletedFullReconcile = hasCompletedFullReconcile,
            };
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(pair));
            CollectingDesktopNotificationService notificationService = new();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();

            for (int run = 0; run < 2; run++)
            {
                controller.ReportStatus(new DesktopSyncStatusSnapshot(
                    [new DesktopSyncPairStatusSnapshot(pairId, "Syncing", null)]));
                controller.ReportStatus(new DesktopSyncStatusSnapshot(
                    [new DesktopSyncPairStatusSnapshot(pairId, "Idle", null, LastSyncedAtUtc: completedAt.AddDays(1))]));
            }

            bool hasPreviousSync = hasCompletedFullReconcile || hasPersistedFileTimestamp;
            int expectedCount = hasPreviousSync ? 0 : 1;
            Assert.That(notificationService.Notifications, Has.Count.EqualTo(expectedCount));
            if (!hasPreviousSync)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Initial sync complete"));
                    Assert.That(notificationService.Notifications[0].Message, Is.EqualTo("Notes is up to date."));
                });
            }
        }

        [Test]
        public async Task StoppedWorker_DoesNotShowEnabledFolderAsDisabledOrPaused()
        {
            Guid pairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(pairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(pairId, "Stopped", null)]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().IsEnabled, Is.True);
                Assert.That(viewModel.IsSyncPaused, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Stopped"));
                Assert.That(viewModel.CurrentProgressText, Is.Not.EqualTo("Enable a folder to start syncing."));
            });
        }
    }
}
