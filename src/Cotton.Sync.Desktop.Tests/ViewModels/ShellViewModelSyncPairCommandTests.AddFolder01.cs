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
        public async Task ShowAddSyncPairCommand_LoadsRemoteRootFolders()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Pictures", "/Pictures"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.Zero);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderSelectionLabel, Is.EqualTo("Cloud folder: /"));
                Assert.That(viewModel.RemoteFolders.Select(static folder => folder.Name), Is.EqualTo(new[] { "Documents", "Pictures" }));
                Assert.That(viewModel.SelectedRemoteFolder, Is.Null);
                Assert.That(viewModel.OpenRemoteFolderCommand.CanExecute(null), Is.False);
                Assert.That(controller.ListRemoteFolderPaths, Is.EqualTo(new[] { "/" }));
            });
        }


        [Test]
        public async Task ShowAddSyncPairCommand_ShowsCloudFolderLoadingStateWhileRemoteFoldersLoad()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            TaskCompletionSource<DesktopRemoteFolderListSnapshot> listCompletion = new TaskCompletionSource<DesktopRemoteFolderListSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                ListRemoteFoldersCompletion = listCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";

            Task commandTask = ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await WaitForAsync(() => viewModel.IsRemoteFolderLoading);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsBusy, Is.True);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.True);
                Assert.That(viewModel.IsRemoteFolderLoadingVisible, Is.True);
                Assert.That(viewModel.RemoteFolderLoadingMessage, Is.EqualTo("Loading cloud folders"));
                Assert.That(viewModel.RemoteFolderWizardPrimaryActionText, Is.EqualTo("Loading cloud folders"));
                Assert.That(viewModel.RemoteFolderWizardPrimaryActionToolTip, Is.EqualTo("Loading cloud folders"));
                Assert.That(viewModel.UseRemoteFolderCommand.CanExecute(null), Is.False);
            });

            listCompletion.SetResult(new DesktopRemoteFolderListSnapshot(
                "/",
                [new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents")]));
            await commandTask;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsRemoteFolderLoading, Is.False);
                Assert.That(viewModel.IsRemoteFolderLoadingVisible, Is.False);
                Assert.That(viewModel.RemoteFolderWizardPrimaryActionText, Is.EqualTo("Use this folder"));
                Assert.That(viewModel.UseRemoteFolderCommand.CanExecute(null), Is.True);
            });
        }


        [Test]
        public async Task RemoteFolderFilter_FiltersLoadedCloudFoldersAndKeepsCurrentFolderSelectable()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Pictures", "/Pictures"),
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Project archive", "/Archive/Project"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            viewModel.SelectedRemoteFolder = viewModel.RemoteFolders.Single(folder => folder.Name == "Documents");

            viewModel.RemoteFolderFilter = "pic";

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.RemoteFolders.Select(static folder => folder.Name), Is.EqualTo(new[] { "Pictures" }));
                Assert.That(viewModel.SelectedRemoteFolder, Is.Null);
                Assert.That(viewModel.HasRemoteFolders, Is.True);
                Assert.That(viewModel.HasNoRemoteFolders, Is.False);
                Assert.That(viewModel.HasRemoteFolderCount, Is.True);
                Assert.That(viewModel.RemoteFolderCountLabel, Is.EqualTo("1 of 3 folders"));
                Assert.That(viewModel.RemoteFolderSelectionLabel, Is.EqualTo("Cloud folder: /"));
            });

            viewModel.RemoteFolderFilter = "missing";

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.RemoteFolders, Is.Empty);
                Assert.That(viewModel.HasRemoteFolders, Is.False);
                Assert.That(viewModel.HasNoRemoteFolders, Is.True);
                Assert.That(viewModel.RemoteFolderCountLabel, Is.EqualTo("0 of 3 folders"));
                Assert.That(viewModel.RemoteFolderEmptyTitle, Is.EqualTo("No matching folders"));
            });

            viewModel.RemoteFolderFilter = string.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.RemoteFolders.Select(static folder => folder.Name), Is.EqualTo(new[] { "Documents", "Pictures", "Project archive" }));
                Assert.That(viewModel.RemoteFolderCountLabel, Is.EqualTo("3 folders"));
                Assert.That(viewModel.RemoteFolderEmptyTitle, Is.EqualTo("No folders here"));
            });
        }


        [Test]
        public async Task ShowAddSyncPairCommand_OpensLocalStepWithoutPromptingForFolder()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Cotton");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.Zero);
                Assert.That(viewModel.LocalFolderPath, Is.Empty);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsDashboardChromeVisible, Is.False);
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.False);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.Empty);
                Assert.That(viewModel.RemoteFolders, Is.Empty);
                Assert.That(controller.ListRemoteFolderPaths, Is.Empty);
            });
        }


        [Test]
        public async Task BrowseLocalFolderCommand_StaysOnLocalStepWhenFolderSelectionIsCanceled()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot("/", []);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(1));
                Assert.That(viewModel.LocalFolderPath, Is.Empty);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.False);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.Empty);
                Assert.That(viewModel.RemoteFolders, Is.Empty);
                Assert.That(controller.ListRemoteFolderPaths, Is.Empty);
            });
        }


        [Test]
        public async Task BrowseLocalFolderCommand_LoadsCloudStepAfterSelection()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Cotton");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(1));
                Assert.That(viewModel.LocalFolderPath, Is.EqualTo("/home/user/Cotton"));
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.False);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.True);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolders.Single().Name, Is.EqualTo("Documents"));
                Assert.That(controller.ListRemoteFolderPaths, Is.EqualTo(new[] { "/" }));
            });
        }


        [TestCase("/home/user/Downloads", "/home/user/Downloads", "This folder is already syncing.")]
        [TestCase("/home/user/Downloads", "/home/user/Downloads/Work", "Sync folders cannot be inside each other.")]
        [TestCase(@"C:\Users\Example\Downloads", @"c:\users\example\downloads\Work", "Sync folders cannot be inside each other.")]
        public async Task BrowseLocalFolderCommand_RejectsExistingOrNestedSyncRootBeforeCloudStep(
            string existingLocalPath,
            string selectedLocalPath,
            string expectedMessage)
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker(selectedLocalPath);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                Guid.NewGuid(),
                "Downloads",
                "Idle",
                localPath: existingLocalPath)));
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(1));
                Assert.That(viewModel.LocalFolderPath, Is.Empty);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.False);
                Assert.That(viewModel.RemoteFolderPath, Is.Empty);
                Assert.That(viewModel.RemoteFolders, Is.Empty);
                Assert.That(controller.ListRemoteFolderPaths, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo(expectedMessage));
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task BrowseLocalFolderCommand_ClearsOverlapErrorWhenNextSelectionIsValid()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Downloads", "/home/user/Cotton");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                Guid.NewGuid(),
                "Downloads",
                "Idle",
                localPath: "/home/user/Downloads")));
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(2));
                Assert.That(viewModel.LocalFolderPath, Is.EqualTo("/home/user/Cotton"));
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.True);
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolders.Single().Name, Is.EqualTo("Documents"));
                Assert.That(controller.ListRemoteFolderPaths, Is.EqualTo(new[] { "/" }));
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
            });
        }
    }
}
