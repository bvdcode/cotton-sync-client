// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public async Task UseRemoteFolderCommand_UsesHighlightedFolderOrCurrentLocation(bool selectChild)
        {
            FakeDesktopShellController controller = new(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/", [new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents")]);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            viewModel.SelectedRemoteFolder = viewModel.RemoteFolders.Single();
            Assert.That(viewModel.RemoteFolderWizardPrimaryActionText, Is.EqualTo("Use selected folder"));
            if (!selectChild)
            {
                viewModel.SelectedRemoteFolder = null;
                Assert.That(viewModel.RemoteFolderWizardPrimaryActionText, Is.EqualTo("Use this folder"));
            }

            await ExecuteAsync(viewModel.UseRemoteFolderCommand);

            Assert.That(controller.AddedSyncPairRequest, Is.Not.Null);
            Assert.That(controller.AddedSyncPairRequest!.RemoteFolderPath,
                Is.EqualTo(selectChild ? "/Documents" : "/"));
        }
    }
}
