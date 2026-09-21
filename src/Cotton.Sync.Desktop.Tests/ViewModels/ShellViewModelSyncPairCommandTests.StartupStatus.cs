// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
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
