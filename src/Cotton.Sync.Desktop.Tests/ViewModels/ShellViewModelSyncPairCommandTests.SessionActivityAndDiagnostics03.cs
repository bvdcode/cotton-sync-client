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
        public async Task OpenConflictCommand_OpensRequestedConflictParentFolder()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Conflict",
                "Reports/q1.txt",
                "Created conflict copy Reports/q1.txt",
                new DateTime(2026, 6, 3, 10, 15, 0, DateTimeKind.Utc),
                syncPairId));
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Conflict",
                "Finance/q2.txt",
                "Created conflict copy Finance/q2.txt",
                new DateTime(2026, 6, 3, 10, 16, 0, DateTimeKind.Utc),
                syncPairId));

            ConflictRowViewModel requestedConflict = viewModel.Conflicts.Single(conflict => conflict.Path == "Finance/q2.txt");
            Assert.That(viewModel.SelectedConflict?.Path, Is.EqualTo("Reports/q1.txt"));
            await ExecuteAsync(viewModel.OpenConflictCommand, requestedConflict);

            Assert.That(controller.OpenedFolderPath, Is.EqualTo(Path.GetFullPath("/home/vadim/Documents/Finance")));
        }


        [Test]
        public async Task OpenConflictCommand_RejectsConflictPathOutsideSyncRoot()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Conflict",
                "../outside.txt",
                "Created conflict copy ../outside.txt",
                new DateTime(2026, 6, 3, 10, 15, 0, DateTimeKind.Utc),
                syncPairId));

            await ExecuteAsync(viewModel.OpenConflictCommand, viewModel.Conflicts.Single());

            Assert.That(controller.OpenedFolderPath, Is.EqualTo(Path.GetFullPath("/home/vadim/Documents")));
        }


        [Test]
        public void ActivityEmptyState_UpdatesWhenActivityIsReported()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNoActivities, Is.True);
                Assert.That(viewModel.HasActivities, Is.False);
            });

            controller.ReportActivity(new DesktopActivitySnapshot(
                "Downloaded",
                "Documents/report.txt",
                "Downloaded Documents/report.txt",
                DateTime.UtcNow));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNoActivities, Is.False);
                Assert.That(viewModel.HasActivities, Is.True);
            });
        }


        [Test]
        public async Task ToggleActivityCommand_TogglesDashboardActivityVisibility()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsActivityVisible, Is.False);
                Assert.That(viewModel.IsActivityHidden, Is.True);
                Assert.That(viewModel.ActivityToggleToolTip, Is.EqualTo("Show activity"));
            });

            await ExecuteAsync(viewModel.ToggleActivityCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsActivityVisible, Is.True);
                Assert.That(viewModel.IsActivityHidden, Is.False);
                Assert.That(viewModel.ActivityToggleToolTip, Is.EqualTo("Hide activity"));
            });
        }
    }
}
