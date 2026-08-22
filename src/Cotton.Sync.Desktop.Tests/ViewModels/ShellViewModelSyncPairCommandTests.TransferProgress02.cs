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
        public async Task TransferProgressChanged_CoalescedCompletionDoesNotLeaveStaleTransfer()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 7, 25, 20, 46, 18, DateTimeKind.Utc);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/1 - Beyond the Edge.m4a",
                TransferredBytes: 50,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/1 - Beyond the Edge.m4a",
                TransferredBytes: 100,
                TotalBytes: 100,
                IsCompleted: true,
                startedAtUtc.AddMilliseconds(100)));

            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.CurrentTransferTitle, Is.Empty);
                Assert.That(row.CurrentOperation, Is.Empty);
                Assert.That(row.HasCurrentProgress, Is.False);
            });
        }


        [Test]
        public async Task TransferProgressChanged_CompletionKeepsAnotherFolderTransferVisible()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = CreateTwoFolderSyncingController(documentsPairId, videosPairId);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 7, 25, 20, 46, 18, DateTimeKind.Utc);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                documentsPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 50,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                videosPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 50,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc.AddSeconds(1)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                videosPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 100,
                TotalBytes: 100,
                IsCompleted: true,
                startedAtUtc.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel documents = viewModel.SyncPairs.Single(pair => pair.Id == documentsPairId);
                SyncPairRowViewModel videos = viewModel.SyncPairs.Single(pair => pair.Id == videosPairId);
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Documents: Uploading report.txt"));
                Assert.That(documents.CurrentOperation, Is.EqualTo("Uploading report.txt"));
                Assert.That(documents.HasCurrentProgress, Is.True);
                Assert.That(videos.CurrentOperation, Is.Empty);
                Assert.That(videos.HasCurrentProgress, Is.False);
            });
        }
    }
}
