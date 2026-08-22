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
        public async Task InitializeAsync_UsesRememberedUsernameWhenRestoredAccountNameIsBlank()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot() with
            {
                AccountName = "   ",
                RememberedUsername = "  desktop@example.test  ",
            });
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            ActivityRowViewModel sessionActivity = viewModel.Activities.First(static activity => activity.Kind == "Account");
            IReadOnlyDictionary<string, string> diagnostics = viewModel.DiagnosticsItems
                .ToDictionary(static item => item.Label, static item => item.Value);
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.AccountName, Is.EqualTo("desktop@example.test"));
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("desktop@example.test"));
                Assert.That(sessionActivity.Path, Is.EqualTo("desktop@example.test"));
                Assert.That(diagnostics["Account"], Is.EqualTo("desktop@example.test"));
            });
        }


        [Test]
        public async Task InitializeAsync_UsesSnapshotDeviceName()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot() with
            {
                DeviceName = "Cotton Sync Desktop (QA-WIN11)",
            });
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.That(viewModel.DeviceName, Is.EqualTo("Cotton Sync Desktop (QA-WIN11)"));
        }


        [Test]
        public async Task ActivityReported_AddsRecentActivityRow()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportActivity(new DesktopActivitySnapshot(
                "Uploaded",
                "Documents/report.txt",
                "Uploaded Documents/report.txt",
                new DateTime(2026, 6, 3, 10, 15, 0, DateTimeKind.Utc)));

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(activity.Kind, Is.EqualTo("Uploaded"));
                Assert.That(activity.Path, Is.EqualTo("Documents/report.txt"));
                Assert.That(activity.Details, Is.EqualTo("Uploaded Documents/report.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_CoalescesHighVolumeTransferBurst()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime startedAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                string path = "Documents/file-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                controller.ReportActivity(new DesktopActivitySnapshot(
                    "Uploaded",
                    path,
                    "Uploaded " + path,
                    startedAtUtc.AddMilliseconds(index * 5),
                    syncPairId));
            }

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 1));
                Assert.That(activity.Kind, Is.EqualTo("Uploaded"));
                Assert.That(activity.Path, Is.EqualTo("Documents/file-099.txt"));
                Assert.That(activity.Details, Is.EqualTo("Uploaded Documents/file-099.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_CoalescesHighVolumeTransferBurstBeforeUiQueue()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime startedAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                string path = "Documents/file-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                controller.ReportActivity(new DesktopActivitySnapshot(
                    "Uploaded",
                    path,
                    "Uploaded " + path,
                    startedAtUtc.AddMilliseconds(index * 5),
                    syncPairId));
            }

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PostedActionCount, Is.EqualTo(1));
                Assert.That(dispatcher.PendingActionCount, Is.EqualTo(1));
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount));
            });

            dispatcher.DrainAll();

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 1));
                Assert.That(activity.Kind, Is.EqualTo("Uploaded"));
                Assert.That(activity.Path, Is.EqualTo("Documents/file-099.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_CoalescesHighVolumePlaceholderBurst()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime startedAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                string path = "Cloud/link-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                controller.ReportActivity(new DesktopActivitySnapshot(
                    "PlaceholderCreated",
                    path,
                    "Made cloud file available " + path,
                    startedAtUtc.AddMilliseconds(index * 5),
                    syncPairId));
            }

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 1));
                Assert.That(activity.Kind, Is.EqualTo("PlaceholderCreated"));
                Assert.That(activity.Path, Is.EqualTo("Cloud/link-099.txt"));
                Assert.That(activity.Details, Is.EqualTo("Made cloud file available Cloud/link-099.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_CoalescesHighVolumePlaceholderBurstBeforeUiQueue()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime startedAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                string path = "Cloud/link-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                controller.ReportActivity(new DesktopActivitySnapshot(
                    "PlaceholderCreated",
                    path,
                    "Made cloud file available " + path,
                    startedAtUtc.AddMilliseconds(index * 5),
                    syncPairId));
            }

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PostedActionCount, Is.EqualTo(1));
                Assert.That(dispatcher.PendingActionCount, Is.EqualTo(1));
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount));
            });

            dispatcher.DrainAll();

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 1));
                Assert.That(activity.Kind, Is.EqualTo("PlaceholderCreated"));
                Assert.That(activity.Path, Is.EqualTo("Cloud/link-099.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_DoesNotCoalesceDifferentSyncPairTransferRows()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(documentsPairId, "Documents", "Syncing"),
                    CreatePair(videosPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime occurredAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            controller.ReportActivity(new DesktopActivitySnapshot(
                "Uploaded",
                "Documents/report.txt",
                "Uploaded Documents/report.txt",
                occurredAtUtc,
                documentsPairId));
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Uploaded",
                "Videos/clip.mp4",
                "Uploaded Videos/clip.mp4",
                occurredAtUtc.AddMilliseconds(10),
                videosPairId));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 2));
                Assert.That(viewModel.Activities[0].Path, Is.EqualTo("Videos/clip.mp4"));
                Assert.That(viewModel.Activities[1].Path, Is.EqualTo("Documents/report.txt"));
            });
        }


        [Test]
        public async Task ActivityReported_DoesNotCoalesceDifferentSyncPairTransfersBeforeUiQueue()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(documentsPairId, "Documents", "Syncing"),
                    CreatePair(videosPairId, "Videos", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            int initialActivityCount = viewModel.Activities.Count;
            DateTime occurredAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            controller.ReportActivity(new DesktopActivitySnapshot(
                "Uploaded",
                "Documents/report.txt",
                "Uploaded Documents/report.txt",
                occurredAtUtc,
                documentsPairId));
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Uploaded",
                "Videos/clip.mp4",
                "Uploaded Videos/clip.mp4",
                occurredAtUtc.AddMilliseconds(10),
                videosPairId));

            Assert.That(dispatcher.PostedActionCount, Is.EqualTo(2));

            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities, Has.Count.EqualTo(initialActivityCount + 2));
                Assert.That(viewModel.Activities[0].Path, Is.EqualTo("Videos/clip.mp4"));
                Assert.That(viewModel.Activities[1].Path, Is.EqualTo("Documents/report.txt"));
            });
        }


        [Test]
        public async Task InitializeAsync_AddsDataPathsToDiagnostics()
        {
            DesktopDataPathSnapshot dataPaths = CreateTestDataPathSnapshot();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            IReadOnlyDictionary<string, string> diagnostics = viewModel.DiagnosticsItems
                .ToDictionary(static item => item.Label, static item => item.Value);

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics["Data folder"], Is.EqualTo(dataPaths.DataDirectory));
                Assert.That(diagnostics["Preferences database"], Is.EqualTo(dataPaths.AppDatabasePath));
                Assert.That(diagnostics["Sync state database"], Is.EqualTo(dataPaths.SyncStateDatabasePath));
                Assert.That(diagnostics["Token store"], Is.EqualTo(dataPaths.TokenStorePath));
            });
        }
    }
}
