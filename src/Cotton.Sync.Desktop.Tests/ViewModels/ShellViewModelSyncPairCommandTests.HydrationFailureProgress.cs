// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task HydrationFailure_ClearsProgressAndAllowsIndependentReaderRetry(
            bool queued,
            bool completeBeforeError)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: queued ? dispatcher : null);
            await viewModel.InitializeAsync();
            DateTime startedAt = DateTime.UtcNow;
            DesktopTransferProgressSnapshot transfer = new(
                syncPairId, SyncTransferDirection.Download, "photo.cr3", 0, 13_000_000, false, startedAt);
            DesktopSyncStatusSnapshot failedStatus = new(
                [new(syncPairId, "Error", "CfHydratePlaceholder failed with HRESULT 0x80070185.")]);
            controller.ReportTransferProgress(transfer);
            dispatcher.DrainAll();
            Assert.That(viewModel.HasCurrentTransfer, Is.True);

            if (!completeBeforeError)
            {
                controller.ReportStatus(failedStatus);
            }
            controller.ReportTransferProgress(transfer with
            {
                IsCompleted = true,
                OccurredAtUtc = startedAt.AddSeconds(1),
            });
            if (completeBeforeError)
            {
                controller.ReportStatus(failedStatus);
            }
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Action required"));
            });

            controller.ReportTransferProgress(transfer with { OccurredAtUtc = startedAt.AddSeconds(2) });
            dispatcher.DrainAll();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.True);
                Assert.That(viewModel.CurrentTransferTitle, Does.Contain("photo.cr3"));
            });

            controller.ReportTransferProgress(transfer with
            {
                IsCompleted = true,
                OccurredAtUtc = startedAt.AddSeconds(3),
            });
            dispatcher.DrainAll();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
            });
        }
    }
}
