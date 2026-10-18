// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [Test]
        public async Task ActivityReported_PreservesFullDetailsAndRemainsAccessibleAfterFailure()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            string details = new string('x', 400) + Environment.NewLine + "Original failure reason.";
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Skipped", "Documents/report.txt", details, DateTime.UtcNow, syncPairId));
            ActivityRowViewModel entry = viewModel.Activities.First();
            viewModel.ToggleActivityCommand.Execute(null);
            controller.ReportStatus(new DesktopSyncStatusSnapshot([
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", "Native conversion failed.")
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(entry.Details, Is.EqualTo(details));
                Assert.That(entry.CopyText, Does.Contain(details));
                Assert.That(entry.CopyText, Does.Contain("Documents/report.txt"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.IsActivityVisible, Is.True);
                Assert.That(viewModel.ToggleActivityCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.ShowSelectedSyncPairEditorCommand.CanExecute(viewModel.SyncPairs.Single()), Is.True);
                Assert.That(viewModel.ExportDiagnosticsCommand.CanExecute(null), Is.True);
            });
        }

        [Test]
        public async Task ActivityReported_CoalescesConvergedBurstBeforeUiQueueAndRetainsFailure()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            DateTime occurredAt = DateTime.UtcNow;
            for (int index = 0; index < 1000; index++)
            {
                string path = "Documents/file-" + index + ".txt";
                controller.ReportActivity(new DesktopActivitySnapshot(
                    "Converged", path, "Synchronized " + path, occurredAt, syncPairId));
            }
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Skipped", "Documents/busy.txt", "File changed during scanning.", occurredAt, syncPairId));

            Assert.That(dispatcher.PendingActionCount, Is.EqualTo(2));
            dispatcher.DrainAll();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Activities.Count(entry => entry.Kind == "Converged"), Is.EqualTo(1));
                Assert.That(viewModel.Activities.First().Details, Is.EqualTo("File changed during scanning."));
            });
        }
    }
}
