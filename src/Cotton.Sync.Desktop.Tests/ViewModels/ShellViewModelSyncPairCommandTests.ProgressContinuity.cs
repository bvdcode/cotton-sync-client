// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task TransferSequence_PreservesWorkPresentationBetweenFiles(bool queued)
        {
            Guid pairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(CreatePair(pairId, "Cloud", "Idle")));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: queued ? dispatcher : null);
            await viewModel.InitializeAsync();
            controller.ReportStatus(new([new(pairId, "Syncing", null)]));
            dispatcher.DrainAll();
            SyncPairRowViewModel row = viewModel.SyncPairs.Single();
            DateTime started = DateTime.UtcNow;
            List<bool> cardVisibility = [];
            List<bool> rowVisibility = [];
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(viewModel.HasCurrentWorkProgress))
                {
                    cardVisibility.Add(viewModel.HasCurrentWorkProgress);
                }
            };
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(row.HasCurrentProgress))
                {
                    rowVisibility.Add(row.HasCurrentProgress);
                }
            };
            for (int index = 0; index < 50; index++)
            {
                DesktopTransferProgressSnapshot transfer = new(
                    pairId, SyncTransferDirection.Download, $"photo-{index}.jpg", 0, 4096, false,
                    started.AddMilliseconds(index * 200));
                controller.ReportTransferProgress(transfer);
                dispatcher.DrainAll();
                controller.ReportTransferProgress(transfer with { TransferredBytes = 4096, IsCompleted = true, OccurredAtUtc = transfer.OccurredAtUtc.AddMilliseconds(100) });
                dispatcher.DrainAll();
                Assert.Multiple(() =>
                {
                    Assert.That(viewModel.HasCurrentTransfer, Is.False);
                    Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                    Assert.That(viewModel.IsStatusCardVisible, Is.False);
                    Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Cloud: Syncing"));
                    Assert.That(viewModel.CurrentWorkProgressDetails, Is.Empty);
                    Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                    Assert.That(row.CurrentOperation, Is.EqualTo("Syncing"));
                    Assert.That(row.HasCurrentProgress, Is.True);
                });
            }
            Assert.Multiple(() =>
            {
                Assert.That(cardVisibility, Is.Not.Empty.And.All.True);
                Assert.That(rowVisibility, Does.Not.Contain(false));
            });
            controller.ReportStatus(new([new(pairId, "Idle", null, LastSyncedAtUtc: DateTime.UtcNow)]));
            dispatcher.DrainAll();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                Assert.That(row.HasCurrentProgress, Is.False);
                Assert.That(row.CurrentOperation, Is.Empty);
            });
        }

        [TestCase("Paused")]
        [TestCase("Disabled")]
        [TestCase("Error")]
        [TestCase("Offline")]
        [TestCase("Waiting")]
        [TestCase("Stopped")]
        public async Task RunningProgress_TerminalStatusImmediatelyEndsWorkPresentation(string status)
        {
            Guid pairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(CreatePair(pairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
            controller.ReportStatus(new([new(pairId, status, null)]));
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
            });
        }
    }
}
