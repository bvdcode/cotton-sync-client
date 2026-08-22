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
        public async Task InitializeAsync_AddsCloudFilesCapabilityAndSyncRootDiagnostics()
        {
            DesktopSyncPairSnapshot virtualFiles = CreatePair(
                Guid.NewGuid(),
                "Documents",
                "Idle",
                mode: SyncPairMode.WindowsVirtualFiles);
            DesktopSyncPairSnapshot fullMirror = CreatePair(Guid.NewGuid(), "Mirror", "Idle");
            DesktopShellSnapshot snapshot = CreateSignedInSnapshot(virtualFiles, fullMirror) with
            {
                PlatformCapabilities = CreatePlatformCapabilities(windowsVirtualFilesSupported: true),
            };
            FakeDesktopShellController controller = new FakeDesktopShellController(snapshot);
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            IReadOnlyDictionary<string, string> diagnostics = viewModel.DiagnosticsItems
                .ToDictionary(static item => item.Label, static item => item.Value);

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics["Windows virtual files"], Is.EqualTo("Supported"));
                Assert.That(diagnostics["Windows virtual files details"], Is.EqualTo("Windows Cloud Files API is available."));
                Assert.That(diagnostics["Documents mode"], Is.EqualTo("Windows virtual files"));
                Assert.That(diagnostics["Documents Cloud Files sync root"], Is.EqualTo("Enabled; connects on sync startup"));
                Assert.That(diagnostics["Mirror mode"], Is.EqualTo("Full mirror"));
                Assert.That(diagnostics["Mirror Cloud Files sync root"], Is.EqualTo("Not used"));
            });
        }


        [Test]
        public async Task OpenDataFolderCommand_OpensDiagnosticsDataDirectory()
        {
            DesktopDataPathSnapshot dataPaths = CreateTestDataPathSnapshot();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Assert.That(viewModel.OpenDataFolderCommand.CanExecute(null), Is.True);

            await ExecuteAsync(viewModel.OpenDataFolderCommand);

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(controller.OpenedFolderPath, Is.EqualTo(dataPaths.DataDirectory));
                Assert.That(activity.Kind, Is.EqualTo("Open"));
                Assert.That(activity.Path, Is.EqualTo(dataPaths.DataDirectory));
                Assert.That(activity.Details, Is.EqualTo("Data folder opened"));
            });
        }


        [Test]
        public async Task ExportDiagnosticsCommand_AddsStatusAndRecentActivity()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                ExportDiagnosticsPath = "/home/vadim/.local/share/Cotton Sync/diagnostics/cotton-sync-diagnostics.zip",
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(controller.ExportDiagnosticsCalls, Is.EqualTo(1));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Diagnostics exported"));
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.True);
                Assert.That(viewModel.LastDiagnosticsBundlePath, Is.EqualTo(controller.ExportDiagnosticsPath));
                Assert.That(viewModel.OpenDiagnosticsBundleFolderCommand.CanExecute(null), Is.True);
                Assert.That(activity.Kind, Is.EqualTo("Diagnostics"));
                Assert.That(activity.Path, Is.EqualTo(controller.ExportDiagnosticsPath));
                Assert.That(activity.Details, Does.Contain(controller.ExportDiagnosticsPath));
            });

            await ExecuteAsync(viewModel.OpenDiagnosticsBundleFolderCommand);

            Assert.That(
                controller.OpenedFolderPath,
                Is.EqualTo(Path.GetDirectoryName(controller.ExportDiagnosticsPath)));
        }


        [Test]
        public async Task ExportDiagnosticsCommand_RunsDuringBackgroundSyncProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")))
            {
                ExportDiagnosticsPath = "/home/vadim/.local/share/Cotton Sync/diagnostics/cotton-sync-diagnostics.zip",
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 10,
                FilesTotal: 100,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));

            await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ExportDiagnosticsCalls, Is.EqualTo(1));
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.True);
                Assert.That(viewModel.LastDiagnosticsBundlePath, Is.EqualTo(controller.ExportDiagnosticsPath));
                Assert.That(viewModel.Activities.First().Kind, Is.EqualTo("Diagnostics"));
            });
        }


        [Test]
        public async Task ExportDiagnosticsCommand_ShowsProgressWithoutBlockingGlobalControls()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")))
            {
                ExportDiagnosticsPath = "/home/vadim/.local/share/Cotton Sync/diagnostics/cotton-sync-diagnostics.zip",
                ExportDiagnosticsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                ExportDiagnosticsCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.ExportDiagnosticsCommand.Execute(null);
            await controller.ExportDiagnosticsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsExportingDiagnostics, Is.True);
                Assert.That(viewModel.IsBusy, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Exporting diagnostics"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Collecting logs and diagnostic state."));
                Assert.That(viewModel.DiagnosticsExportProgressMessage, Is.EqualTo("Collecting logs and diagnostic state."));
                Assert.That(viewModel.ExportDiagnosticsCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.OpenDataFolderCommand.CanExecute(null), Is.True);
            });

            controller.ExportDiagnosticsCompletion.SetResult(controller.ExportDiagnosticsPath);
            await WaitForAsync(() => !viewModel.ExportDiagnosticsCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsExportingDiagnostics, Is.False);
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Diagnostics exported"));
            });
        }


        [Test]
        public async Task ExportDiagnosticsCommand_DoesNotRestoreResolvedActionRequiredState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")))
            {
                ExportDiagnosticsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                ExportDiagnosticsCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", "Initial sync error."),
            ]));

            viewModel.ExportDiagnosticsCommand.Execute(null);
            await controller.ExportDiagnosticsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null),
            ]));
            string resolvedGlobalStatus = viewModel.GlobalStatus;
            controller.ExportDiagnosticsCompletion.SetResult(controller.ExportDiagnosticsPath);
            await WaitForAsync(() => !viewModel.ExportDiagnosticsCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo(resolvedGlobalStatus));
            });
        }


        [Test]
        public async Task ExportDiagnosticsCommand_YieldsProgressBeforeStartingExport()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                ExportDiagnosticsCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            QueuedAccessDesktopUiDispatcher dispatcher = new QueuedAccessDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();

            viewModel.ExportDiagnosticsCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsExportingDiagnostics, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Exporting diagnostics"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Collecting logs and diagnostic state."));
                Assert.That(controller.ExportDiagnosticsCalls, Is.Zero);
                Assert.That(dispatcher.PendingActionCount, Is.EqualTo(1));
            });

            dispatcher.DrainAll();
            await WaitForAsync(() => controller.ExportDiagnosticsCalls == 1);
            controller.ExportDiagnosticsCompletion.SetResult(controller.ExportDiagnosticsPath);
            await WaitForAsync(() => !viewModel.ExportDiagnosticsCommand.IsRunning);
        }


        [Test]
        public async Task ExportDiagnosticsCommand_ReportsFailureAsActionRequired()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                ExportDiagnosticsException = new IOException("There is not enough space on the disk."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

            ActivityRowViewModel activity = viewModel.Activities.First();
            Assert.Multiple(() =>
            {
                Assert.That(controller.ExportDiagnosticsCalls, Is.EqualTo(1));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("This computer does not have enough free disk space for sync. Free space and retry."));
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.False);
                Assert.That(viewModel.OpenDiagnosticsBundleFolderCommand.CanExecute(null), Is.False);
                Assert.That(activity.Kind, Is.EqualTo("Error"));
                Assert.That(activity.Details, Is.EqualTo(viewModel.ActionRequiredMessage));
            });
        }


        [Test]
        public async Task ConflictActivity_AddsConflictRow()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                syncPairId,
                "Documents",
                "Idle",
                new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc))));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportActivity(new DesktopActivitySnapshot(
                "Conflict",
                "Documents/report.txt",
                "Created conflict copy Documents/report.txt",
                new DateTime(2026, 6, 3, 10, 15, 0, DateTimeKind.Utc),
                syncPairId));

            ConflictRowViewModel conflict = viewModel.Conflicts.Single();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasConflicts, Is.True);
                Assert.That(viewModel.HasStatusAttention, Is.True);
                Assert.That(viewModel.ConflictCountLabel, Is.EqualTo("1 conflict"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Conflicts need review"));
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Review conflicts below to continue syncing."));
                Assert.That(viewModel.SelectedConflict, Is.SameAs(conflict));
                Assert.That(conflict.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(conflict.Path, Is.EqualTo("Documents/report.txt"));
                Assert.That(conflict.Details, Is.EqualTo("Created conflict copy Documents/report.txt"));
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Conflict"));
                Assert.That(viewModel.SyncPairs.Single().IsStatusAttention, Is.True);
                Assert.That(viewModel.Activities.First().Kind, Is.EqualTo("Conflict"));
            });
        }
    }
}
