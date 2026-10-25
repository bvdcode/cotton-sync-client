// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task CloudFilesFailure_PreservesFullDetailsWithoutRequestingUserActionDuringOtherTransfers(bool dehydration)
        {
            Guid pairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(CreatePair(pairId, "Cloud", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            string path = "Lessons/" + new string('a', 200) + "/Видео.mp4";
            string message = dehydration
                ? VirtualFileUserFacingCopy.CreateDehydrationIncompleteMessage([path, "Lessons/second.mp4"])
                : VirtualFileUserFacingCopy.CreateMetadataIncompleteMessage([path, "Lessons/second.mp4"]);
            string title = dehydration ? "Free up space incomplete" : "Sync incomplete";

            foreach (string status in new[] { "Error", "Syncing", "Error" })
            {
                controller.ReportStatus(new([new(pairId, status, message)]));
                Assert.Multiple(() =>
                {
                    Assert.That(viewModel.DashboardStatusTitle, Is.EqualTo(title));
                    Assert.That(viewModel.HeaderStatusText, Is.EqualTo(title));
                    Assert.That(viewModel.DashboardStatusDetails, Is.EqualTo(message));
                    Assert.That(viewModel.CanRetryActionRequired, Is.False);
                    Assert.That(viewModel.CanApproveRemoteMassDelete, Is.False);
                    Assert.That(viewModel.CurrentProgressText, Does.Not.Contain("Fix the issue"));
                });
            }

            DesktopTrayStatus trayStatus = DesktopTrayStatusResolver.FromShellState(
                true, viewModel.HeaderStatusText, viewModel.HasStatusAttention);
            Assert.That(trayStatus.ToolTipText, Does.Contain(title));
            controller.ReportStatus(new([new(pairId, "Idle", null)]));
            Assert.That(viewModel.HasActionRequired, Is.False);
        }
    }
}
