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
        public async Task MissingDesktopSyncChangesApi_BlocksAddFolderFlowWithoutReplacingTheServerError()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Downloads");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                Guid.NewGuid(),
                "Downloads",
                "Idle",
                localPath: "/home/user/Downloads")))
            {
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot(
                        "Desktop sync change feed",
                        false,
                        "Cotton API request GET /api/v1/sync/changes?since=0&limit=1 returned invalid JSON "
                        + "with content type 'text/html' and status 200 (OK)."),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            viewModel.RemoteFolderPath = "/";

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.ShowAddSyncPairCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.BrowseLocalFolderCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.SelfTestCommand);
            viewModel.BrowseLocalFolderCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.Zero);
                Assert.That(viewModel.ShowAddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.BrowseLocalFolderCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
            });

            await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.ShowAddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.BrowseLocalFolderCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.True);
            });
        }


        [Test]
        public async Task SelfTestPass_ClearsMissingDesktopSyncChangesApiAddFolderBlock()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot(
                        "Desktop sync change feed",
                        false,
                        "Cotton API request GET /api/v1/sync/changes?since=0&limit=1 returned invalid JSON "
                        + "with content type 'text/html' and status 200 (OK)."),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            viewModel.RemoteFolderPath = "/";

            await ExecuteAsync(viewModel.SelfTestCommand);
            Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);

            controller.SelfTestSnapshot = new DesktopSelfTestSnapshot(
            [
                new DesktopSelfTestItemSnapshot("Desktop sync change feed", true, "Ready"),
            ]);

            await ExecuteAsync(viewModel.SelfTestCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Self-test passed"));
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.True);
            });
        }


        [Test]
        public async Task SelfTest_BlocksAddFolderWhenMissingSyncApiFailureIsNotFirst()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot("Token storage", false, "Restricted file storage is not release-secure."),
                    new DesktopSelfTestItemSnapshot(
                        "Desktop sync change feed",
                        false,
                        "Cotton API request GET /api/v1/sync/changes?since=0&limit=1 returned invalid JSON "
                        + "with content type 'text/html' and status 200 (OK)."),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            viewModel.RemoteFolderPath = "/";

            await ExecuteAsync(viewModel.SelfTestCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Restricted file storage is not release-secure."));
                Assert.That(viewModel.ShowAddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task StatusChanged_MissingDesktopSyncChangesApiBlocksAddFolderFlow()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            viewModel.RemoteFolderPath = "/";

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Error",
                    "Cotton API request GET /api/v1/sync/changes?since=0&limit=1 returned invalid JSON "
                    + "with content type 'text/html' and status 200 (OK)."),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
                Assert.That(viewModel.ShowAddSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.AddSyncPairCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task CancelAddSyncPairCommand_ClearsLocalFolderOverlapError()
        {
            Guid existingPairId = Guid.NewGuid();
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Downloads");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                existingPairId,
                "Downloads",
                "Idle",
                localPath: "/home/user/Downloads")));
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            await ExecuteAsync(viewModel.CancelAddSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.IsDashboardChromeVisible, Is.True);
                Assert.That(viewModel.LocalFolderPath, Is.Empty);
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.SyncPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { existingPairId }));
                Assert.That(controller.AddedSyncPairRequest, Is.Null);
                Assert.That(controller.CreatedRemoteFolders, Is.Empty);
                Assert.That(controller.ListRemoteFolderPaths, Is.Empty);
            });
        }


        [Test]
        public async Task CreateRemoteFolderCommand_CreatesFolderAndUsesItAsCurrentCloudTarget()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Cotton");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot("/", []);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);

            await ExecuteAsync(viewModel.ShowCreateRemoteFolderCommand);
            viewModel.NewRemoteFolderName = "  Projects  ";
            await ExecuteAsync(viewModel.CreateRemoteFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsCreateRemoteFolderVisible, Is.False);
                Assert.That(viewModel.NewRemoteFolderName, Is.Empty);
                Assert.That(controller.CreatedRemoteFolders, Is.EqualTo(new[] { ("/", "Projects") }));
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/Projects"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/Projects"));
                Assert.That(viewModel.RemoteFolderSelectionLabel, Is.EqualTo("Cloud folder: /Projects"));
                Assert.That(viewModel.HasActionRequired, Is.False);
            });
        }


        [Test]
        public async Task OpenRemoteFolderCommand_NavigatesToSelectedCloudFolder()
        {
            Guid archiveId = Guid.NewGuid();
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker("/home/user/Cotton");
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                ]);
            controller.RemoteFoldersByPath["/Documents"] = new DesktopRemoteFolderListSnapshot(
                "/Documents",
                [
                    new DesktopRemoteFolderSnapshot(archiveId, "Archive", "/Documents/Archive"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.ShowAddSyncPairCommand);
            await ExecuteAsync(viewModel.BrowseLocalFolderCommand);
            viewModel.SelectedRemoteFolder = viewModel.RemoteFolders.Single();

            await ExecuteAsync(viewModel.OpenRemoteFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/Documents"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/Documents"));
                Assert.That(viewModel.RemoteFolderSelectionLabel, Is.EqualTo("Cloud folder: /Documents"));
                Assert.That(viewModel.RemoteFolders.Single().Id, Is.EqualTo(archiveId));
                Assert.That(viewModel.SelectedRemoteFolder, Is.Null);
                Assert.That(viewModel.OpenRemoteFolderCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.RemoteFolderUpCommand.CanExecute(null), Is.True);
            });
        }


        [Test]
        public async Task UseRemoteFolderCommand_AddsSyncPairInAddMode()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.LocalFolderPath = "/home/user/Cotton";
            viewModel.RemoteFolderPath = "/Documents";

            await ExecuteAsync(viewModel.UseRemoteFolderCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.AddedSyncPairRequest, Is.Not.Null);
                Assert.That(controller.AddedSyncPairRequest!.LocalFolderPath, Is.EqualTo("/home/user/Cotton"));
                Assert.That(controller.AddedSyncPairRequest.RemoteFolderPath, Is.EqualTo("/Documents"));
                Assert.That(controller.AddedSyncPairRequest.Mode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(viewModel.SyncPairs, Has.Count.EqualTo(1));
                Assert.That(viewModel.SyncPairs.Single().RemotePath, Is.EqualTo("/Documents"));
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sync requested"));
            });
        }
    }
}
