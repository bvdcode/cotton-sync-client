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
        public async Task TransferProgressChanged_CompletionRestoresInitialPopulationProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 7, 25, 20, 46, 18, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 160_000,
                FilesTotal: 477_153,
                CurrentPath: "Music/album/track.m4a",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(50),
                Causes: SyncRunCause.InitialPopulation,
                RequestedPathCount: 1,
                IsFull: true));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 161_000,
                FilesTotal: 477_153,
                CurrentPath: "Music/album/track.m4a",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(50).AddSeconds(10),
                Causes: SyncRunCause.InitialPopulation,
                RequestedPathCount: 1,
                IsFull: true));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/1 - Beyond the Edge.m4a",
                TransferredBytes: 2L * 1024 * 1024 * 1024,
                TotalBytes: 3L * 1024 * 1024 * 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(50).AddSeconds(11)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/1 - Beyond the Edge.m4a",
                TransferredBytes: 3L * 1024 * 1024 * 1024,
                TotalBytes: 3L * 1024 * 1024 * 1024,
                IsCompleted: true,
                OccurredAtUtc: startedAtUtc.AddMinutes(50).AddSeconds(12)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing cloud files"));
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Cloud"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Contain("161000 cloud items ready"));
            });

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 161_500,
                FilesTotal: 477_153,
                CurrentPath: "Music/album/next-track.m4a",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(50).AddSeconds(18),
                Causes: SyncRunCause.InitialPopulation,
                RequestedPathCount: 1,
                IsFull: true));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 162_000,
                FilesTotal: 477_153,
                CurrentPath: "Music/album/next-track.m4a",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMinutes(50).AddSeconds(23),
                Causes: SyncRunCause.InitialPopulation,
                RequestedPathCount: 1,
                IsFull: true));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("3.0 GB"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Contain("cloud items/s"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("GB/s"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("MB/s"));
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsFreeingUpSpaceProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Music", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.DehydratingCloudFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Albums/Track 101.flac",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10),
                Causes: SyncRunCause.LocalChange,
                RequestedPathCount: 1));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Music"));
                Assert.That(
                    viewModel.CurrentWorkProgressDetails,
                    Is.EqualTo("Freeing up space · 100 of 1000 files · Local change · 1 changed path"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(10).Within(0.01));
                Assert.That(viewModel.CurrentTrayActivityKind, Is.EqualTo(DesktopTrayActivityKind.FreeingSpace));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
                Assert.That(viewModel.SyncPairs.Single().CurrentOperation, Is.EqualTo("Freeing up space 100 of 1000"));
            });
        }
    }
}
