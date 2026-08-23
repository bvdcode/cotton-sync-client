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
        public async Task AddSyncPairFlow_CreatesDesktopPairAndRequestsInitialSync()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker(@"C:\Users\QA\Desktop");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot("/", []);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);
            await ExecuteAsync(viewModel.ShowCreateRemoteFolderCommand);
            viewModel.NewRemoteFolderName = "Desktop";
            await ExecuteAsync(viewModel.CreateRemoteFolderCommand);
            await ExecuteAsync(viewModel.UseRemoteFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(1));
                Assert.That(controller.CreatedRemoteFolders, Is.EqualTo(new[] { ("/", "Desktop") }));
                Assert.That(controller.AddedSyncPairRequest, Is.Not.Null);
                Assert.That(controller.AddedSyncPairRequest!.LocalFolderPath, Is.EqualTo(@"C:\Users\QA\Desktop"));
                Assert.That(controller.AddedSyncPairRequest.RemoteFolderPath, Is.EqualTo("/Desktop"));
                Assert.That(controller.AddedSyncPairRequest.Mode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(viewModel.SyncPairs, Has.Count.EqualTo(1));
                Assert.That(viewModel.SyncPairs.Single().LocalPath, Is.EqualTo(@"C:\Users\QA\Desktop"));
                Assert.That(viewModel.SyncPairs.Single().RemotePath, Is.EqualTo("/Desktop"));
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sync requested"));
                Assert.That(viewModel.HasActionRequired, Is.False);
            });
        }


        [Test]
        public async Task AddSyncPairFlow_CanCreateWindowsVirtualFilesPairWhenSupported()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker(@"C:\Users\QA\Desktop");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(
                platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: true)));
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot("/", []);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);
            viewModel.RemoteFolderPath = "/Desktop";
            viewModel.IsWindowsVirtualFilesSyncModeSelected = true;
            await ExecuteAsync(viewModel.UseRemoteFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.AddedSyncPairRequest, Is.Not.Null);
                Assert.That(controller.AddedSyncPairRequest!.LocalFolderPath, Is.EqualTo(@"C:\Users\QA\Desktop"));
                Assert.That(controller.AddedSyncPairRequest.RemoteFolderPath, Is.EqualTo("/Desktop"));
                Assert.That(controller.AddedSyncPairRequest.Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                Assert.That(viewModel.SyncPairs, Has.Count.EqualTo(1));
                Assert.That(viewModel.SyncPairs.Single().Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                Assert.That(viewModel.SelectedSyncMode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sync requested"));
            });
        }


        [Test]
        public async Task UseRemoteFolderCommand_ClosesWizardAndShowsGlobalSetupProgressWhileAddPairIsPending()
        {
            Guid existingPairId = Guid.NewGuid();
            TaskCompletionSource<SyncPairSettings> addPairCompletion = new TaskCompletionSource<SyncPairSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(
                CreatePlatformCapabilities(windowsVirtualFilesSupported: true),
                CreatePair(existingPairId, "Documents", "Idle")))
            {
                AddSyncPairCompletion = addPairCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = @"C:\Users\QA\Cloud";
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            viewModel.RemoteFolderPath = "/";
            viewModel.IsWindowsVirtualFilesSyncModeSelected = true;

            Task commandTask = ExecuteAsync(viewModel.UseRemoteFolderCommand);
            await WaitForAsync(() => controller.AddedSyncPairRequest is not null && viewModel.IsAddingSyncPair);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsBusy, Is.False);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(
                    viewModel.AddSyncPairSetupProgressMessage,
                    Is.EqualTo("Connecting virtual files"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Connecting virtual files"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Adding sync folder"));
                Assert.That(viewModel.UseRemoteFolderCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.True);
            });

            addPairCompletion.SetResult(new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Cloud",
                LocalRootPath = @"C:\Users\QA\Cloud",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await commandTask;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsAddingSyncPair, Is.False);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sync requested"));
            });
        }

        [Test]
        public async Task UseRemoteFolderCommand_ReopensWizardWithSelectionWhenSetupFails()
        {
            TaskCompletionSource<SyncPairSettings> addPairCompletion =
                new TaskCompletionSource<SyncPairSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(
                platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: true)))
            {
                AddSyncPairCompletion = addPairCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = @"C:\Users\QA\Cloud";
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            viewModel.RemoteFolderPath = "/Documents";
            viewModel.IsWindowsVirtualFilesSyncModeSelected = true;

            Task commandTask = ExecuteAsync(viewModel.UseRemoteFolderCommand);
            await WaitForAsync(() => controller.AddedSyncPairRequest is not null && !viewModel.IsAddSyncPairWizardVisible);
            addPairCompletion.SetException(new InvalidOperationException("Registration failed."));
            await commandTask;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsAddingSyncPair, Is.False);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.LocalFolderPath, Is.EqualTo(@"C:\Users\QA\Cloud"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/Documents"));
                Assert.That(viewModel.IsWindowsVirtualFilesSyncModeSelected, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.ActionRequiredMessage, Does.Contain("Registration failed"));
            });
        }
    }
}
