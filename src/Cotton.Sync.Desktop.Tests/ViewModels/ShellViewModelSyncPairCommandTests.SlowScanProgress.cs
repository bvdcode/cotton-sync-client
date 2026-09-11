// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [Test]
        public async Task StatusChanged_KeepsRemoteScanDetailsWhileActiveWithoutRecentProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAt = DateTime.UtcNow;
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 502,
                FilesTotal: null,
                CurrentPath: "Documents/report.txt",
                StartedAtUtc: startedAt,
                IsCompleted: false,
                OccurredAtUtc: startedAt,
                Causes: SyncRunCause.Periodic | SyncRunCause.RealtimeRemoteChange,
                IsFull: false,
                RequestedPathCount: 1000));
            string details = viewModel.CurrentRunProgressDetails;

            await Task.Delay(TimeSpan.FromSeconds(11));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null, CurrentOperation: "Syncing changes")]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo(details));
                Assert.That(viewModel.CurrentRunProgressDetails, Does.Contain("502"));
                Assert.That(viewModel.SyncPairs.Single().CurrentOperation, Is.EqualTo("Checking cloud"));
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
            });

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null)]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
            });
        }
    }
}
