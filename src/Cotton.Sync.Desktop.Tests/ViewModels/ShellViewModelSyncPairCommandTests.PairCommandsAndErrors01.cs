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
        public void SelfTestItemRowViewModel_TracksDetailsAvailability()
        {
            SelfTestItemRowViewModel item = new SelfTestItemRowViewModel
            {
                Details = "Server identity check failed with a long supportable explanation.",
            };

            item.Details = string.Empty;

            Assert.That(item.HasDetails, Is.False);
        }


        [Test]
        public async Task ToggleSelectedSyncPairEnabledCommand_DisablesSelectedPair()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ToggleSelectedSyncPairEnabledCommand);

            SyncPairRowViewModel selected = viewModel.SelectedSyncPair!;
            Assert.Multiple(() =>
            {
                Assert.That(controller.EnabledSyncPairId, Is.EqualTo(syncPairId));
                Assert.That(controller.EnabledSyncPairValue, Is.False);
                Assert.That(selected.IsEnabled, Is.False);
                Assert.That(selected.IsDisabled, Is.True);
                Assert.That(selected.ToggleEnabledLabel, Is.EqualTo("Enable sync folder"));
                Assert.That(selected.Status, Is.EqualTo("Disabled"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Folder disabled"));
            });
        }


        [Test]
        public async Task ToggleSelectedSyncPairEnabledCommand_KeepsOtherPairsPausedWhenDisablingDuringGlobalPause()
        {
            Guid disabledSyncPairId = Guid.NewGuid();
            Guid otherSyncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(disabledSyncPairId, "Cloud", "Paused"),
                    CreatePair(otherSyncPairId, "Videos", "Paused")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ToggleSelectedSyncPairEnabledCommand);

            SyncPairRowViewModel disabledPair = viewModel.SyncPairs.Single(pair => pair.Id == disabledSyncPairId);
            SyncPairRowViewModel otherPair = viewModel.SyncPairs.Single(pair => pair.Id == otherSyncPairId);
            Assert.Multiple(() =>
            {
                Assert.That(controller.EnabledSyncPairId, Is.EqualTo(disabledSyncPairId));
                Assert.That(controller.EnabledSyncPairValue, Is.False);
                Assert.That(disabledPair.Status, Is.EqualTo("Disabled"));
                Assert.That(disabledPair.IsEnabled, Is.False);
                Assert.That(otherPair.Status, Is.EqualTo("Paused"));
                Assert.That(otherPair.IsEnabled, Is.True);
                Assert.That(viewModel.IsSyncPaused, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
            });
        }


        [Test]
        public async Task RemoveSelectedSyncPairCommand_RequiresConfirmationBeforeRemovingPair()
        {
            Guid firstSyncPairId = Guid.NewGuid();
            Guid secondSyncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(firstSyncPairId, "Documents", "Idle"),
                    CreatePair(secondSyncPairId, "Pictures", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RemovedSyncPairId, Is.Null);
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.True);
                Assert.That(viewModel.SelectedSyncPair?.IsEditorVisible, Is.True);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.True);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationActionsVisible, Is.True);
                Assert.That(viewModel.RemoveSyncPairConfirmationTitle, Is.EqualTo("Remove Documents?"));
                Assert.That(viewModel.RemoveSyncPairConfirmationMessage, Is.EqualTo("Stops syncing this folder. Local files stay on this device; cloud files stay online."));
                Assert.That(viewModel.ConfirmRemoveSelectedSyncPairCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.RemoveSelectedSyncPairCommand.CanExecute(null), Is.False);
            });

            await ExecuteAsync(viewModel.CancelRemoveSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RemovedSyncPairId, Is.Null);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.False);
                Assert.That(viewModel.RemoveSelectedSyncPairCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);
            await ExecuteAsync(viewModel.ConfirmRemoveSelectedSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RemovedSyncPairId, Is.EqualTo(firstSyncPairId));
                Assert.That(viewModel.SyncPairs, Has.Count.EqualTo(1));
                Assert.That(viewModel.SyncPairs.Single().Id, Is.EqualTo(secondSyncPairId));
                Assert.That(viewModel.SelectedSyncPair?.Id, Is.EqualTo(secondSyncPairId));
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.False);
                Assert.That(viewModel.SyncPairs.Single().IsEditorVisible, Is.False);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Ready"));
                Assert.That(viewModel.CurrentProgressText, Does.Not.Contain("Removing"));
            });
        }


        [Test]
        public async Task RemoveSelectedSyncPairCommand_NotifiesConfirmationActionsVisibility()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Idle", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            List<string?> changedProperties = new();
            viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.True);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationActionsVisible, Is.True);
                Assert.That(
                    changedProperties,
                    Does.Contain(nameof(ShellViewModel.IsRemoveSyncPairConfirmationActionsVisible)));
            });
        }


        [Test]
        public async Task RemoveSelectedSyncPairCommand_WarnsBeforeRemovingVirtualFilesPair()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Desktop", "Idle", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.RemoveSyncPairConfirmationTitle, Is.EqualTo("Remove Desktop?"));
                Assert.That(viewModel.RemoveSyncPairConfirmationMessage, Is.EqualTo("Stops syncing this folder. Cloud files stay online; the local placeholder folder is removed when it has no regular local files."));
            });
        }


        [Test]
        public async Task ConfirmRemoveSelectedSyncPairCommand_ShowsProgressWhileVirtualFilesRootIsRemoved()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Desktop", "Idle", mode: SyncPairMode.WindowsVirtualFiles)))
            {
                RemoveSyncPairStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                RemoveSyncPairCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);

            viewModel.ConfirmRemoveSelectedSyncPairCommand.Execute(null);
            await controller.RemoveSyncPairStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsRemovingSyncPair, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Removing sync folder"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Removing Cloud Files sync root and cleaning local placeholder folder. Large online-only folders can take a few minutes."));
                Assert.That(viewModel.RemoveSyncPairProgressMessage, Is.EqualTo("Removing Cloud Files sync root and cleaning local placeholder folder. Large online-only folders can take a few minutes."));
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.True);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationActionsVisible, Is.False);
                Assert.That(viewModel.RemoveSyncPairConfirmationTitle, Is.EqualTo("Removing Desktop"));
                Assert.That(
                    viewModel.RemoveSyncPairConfirmationMessage,
                    Is.EqualTo("Removing the Cloud Files registration and local placeholder folder. This can take a few minutes for large online-only folders."));
                Assert.That(viewModel.ConfirmRemoveSelectedSyncPairCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.CancelRemoveSyncPairCommand.CanExecute(null), Is.False);
            });

            controller.RemoveSyncPairCompletion.SetResult();
            await WaitForAsync(() => !viewModel.ConfirmRemoveSelectedSyncPairCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsRemovingSyncPair, Is.False);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.False);
                Assert.That(viewModel.SyncPairs, Is.Empty);
            });
        }


        [Test]
        public async Task ConfirmRemoveSelectedSyncPairCommand_RunsRemovalAwayFromCallerThread()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Desktop", "Idle", mode: SyncPairMode.WindowsVirtualFiles)))
            {
                RemoveSyncPairStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                RemoveSyncPairCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.RemoveSelectedSyncPairCommand);

            int callerThreadId = Environment.CurrentManagedThreadId;
            viewModel.ConfirmRemoveSelectedSyncPairCommand.Execute(null);
            await controller.RemoveSyncPairStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsRemovingSyncPair, Is.True);
                Assert.That(controller.RemoveSyncPairThreadId, Is.Not.EqualTo(callerThreadId));
            });

            controller.RemoveSyncPairCompletion.SetResult();
            await WaitForAsync(() => !viewModel.ConfirmRemoveSelectedSyncPairCommand.IsRunning);
        }


        [Test]
        public async Task ShowSelectedSyncPairEditorCommand_OpensControlsForCommandParameter()
        {
            Guid firstSyncPairId = Guid.NewGuid();
            Guid secondSyncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(firstSyncPairId, "Documents", "Idle"),
                    CreatePair(secondSyncPairId, "Pictures", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            SyncPairRowViewModel firstPair = viewModel.SyncPairs.Single(pair => pair.Id == firstSyncPairId);
            SyncPairRowViewModel secondPair = viewModel.SyncPairs.Single(pair => pair.Id == secondSyncPairId);

            await ExecuteAsync(viewModel.ShowSelectedSyncPairEditorCommand, secondPair);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SelectedSyncPair?.Id, Is.EqualTo(secondSyncPairId));
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.True);
                Assert.That(firstPair.IsEditorVisible, Is.False);
                Assert.That(secondPair.IsEditorVisible, Is.True);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.False);
                Assert.That(viewModel.CancelSelectedSyncPairEditorCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.ShowSelectedSyncPairEditorCommand, secondPair);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SelectedSyncPair?.Id, Is.EqualTo(secondSyncPairId));
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.False);
                Assert.That(secondPair.IsEditorVisible, Is.False);
                Assert.That(viewModel.CancelSelectedSyncPairEditorCommand.CanExecute(null), Is.False);
            });

            await ExecuteAsync(viewModel.ShowSelectedSyncPairEditorCommand, secondPair);
            await ExecuteAsync(viewModel.CancelSelectedSyncPairEditorCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.False);
                Assert.That(secondPair.IsEditorVisible, Is.False);
                Assert.That(viewModel.CancelSelectedSyncPairEditorCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task SaveSelectedSyncPairNameCommand_PersistsTrimmedName()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.SelectedSyncPair!.EditableDisplayName = "  Work documents  ";

            await ExecuteAsync(viewModel.SaveSelectedSyncPairNameCommand);

            SyncPairRowViewModel selected = viewModel.SelectedSyncPair!;
            Assert.Multiple(() =>
            {
                Assert.That(controller.RenamedSyncPairId, Is.EqualTo(syncPairId));
                Assert.That(controller.RenamedSyncPairDisplayName, Is.EqualTo("Work documents"));
                Assert.That(selected.DisplayName, Is.EqualTo("Work documents"));
                Assert.That(selected.EditableDisplayName, Is.EqualTo("Work documents"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Folder renamed"));
                Assert.That(viewModel.HasActionRequired, Is.False);
            });
        }
    }
}
