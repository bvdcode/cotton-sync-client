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
        public async Task StatusChanged_ShowsOfflineAsDistinctGlobalStatus()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Offline", "Cannot reach Cotton Cloud"),
            ]));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Offline"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Offline"));
                Assert.That(viewModel.StatusCardTitle, Is.EqualTo("Offline"));
                Assert.That(viewModel.HasOfflineStatus, Is.True);
                Assert.That(viewModel.HasStatusAttention, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Waiting for connection to recover."));
                Assert.That(row.Status, Is.EqualTo("Offline"));
                Assert.That(row.LastError, Is.EqualTo("Cannot reach Cotton Cloud"));
            });
        }


        [Test]
        public async Task StatusChanged_ShowsWaitingForLocalFileWithoutActionRequired()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            const string message = "Local file is not ready yet: Drafts/report.docx. Sync will retry.";

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Waiting", message, message),
            ]));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Waiting"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Waiting"));
                Assert.That(viewModel.HasWaitingStatus, Is.True);
                Assert.That(viewModel.HasStatusAttention, Is.False);
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: " + message));
                Assert.That(viewModel.Activities, Has.None.Matches<ActivityRowViewModel>(activity => activity.Kind == "Error"));
                Assert.That(row.Status, Is.EqualTo("Waiting"));
                Assert.That(row.IsStatusWaiting, Is.True);
                Assert.That(row.IsStatusAttention, Is.False);
            });
        }


        [Test]
        public async Task StatusChanged_ActionRequiredTakesVisualPriorityOverWaiting()
        {
            Guid waitingPairId = Guid.NewGuid();
            Guid errorPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(
                CreatePair(waitingPairId, "Documents", "Idle"),
                CreatePair(errorPairId, "Photos", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(waitingPairId, "Waiting", "Document is locked."),
                new DesktopSyncPairStatusSnapshot(errorPairId, "Error", "Local folder is missing."),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.HasStatusAttention, Is.True);
                Assert.That(viewModel.HasWaitingStatus, Is.False);
            });
        }


        [Test]
        public async Task StatusChanged_ClearsOfflineStateAfterConnectionRecovers()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Offline",
                    "Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."),
            ]));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null, LastSyncedAtUtc: DateTime.UtcNow),
            ]));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.HasOfflineStatus, Is.False);
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(row.Status, Is.EqualTo("Idle"));
                Assert.That(row.LastError, Is.Null);
            });
        }


        [Test]
        public async Task StatusChanged_UsesHumanDiskFullActionRequiredMessage()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", "No space left on device"),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("This computer does not have enough free disk space for sync. Free space and retry."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsHydrationProgressStartingState()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.HydrationProgress);

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Making files available · 1 of 2000 files"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Downloading track-0040.flac"));
                Assert.That(row.HasCurrentProgress, Is.True);
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsDehydrationProgressStartingState()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.DehydrationProgress);

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Freeing up space · 1 of 1000 files"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Freeing up space 1 of 1000"));
                Assert.That(row.HasCurrentProgress, Is.True);
            });
        }


        [Test]
        public async Task StatusChanged_UsesHumanRemoteMassDeleteGuardActionRequiredMessage()
        {
            Guid syncPairId = Guid.NewGuid();
            const string expectedMessage =
                "Cotton Sync blocked a large remote delete plan (2207 pending deletes exceed limit 100). "
                + "Check local files and Cotton Cloud, then explicitly approve the exact delete plan.";
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Music", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Error",
                    "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100. "
                    + "Plan fingerprint " + RemoteDeletePlanFingerprint + "."),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Action required"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.CanApproveRemoteMassDelete, Is.True);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo(expectedMessage));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task StatusChanged_AddsHumanErrorActivityMessage()
        {
            Guid syncPairId = Guid.NewGuid();
            const string rawError = "'<' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.";
            const string expectedMessage =
                "Cotton API returned a web page instead of JSON. Check the server URL or backend deployment and retry.";
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", rawError),
            ]));

            ActivityRowViewModel errorActivity = viewModel.Activities.First(activity => activity.Kind == "Error");
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().LastError, Is.EqualTo(rawError));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo(expectedMessage));
                Assert.That(errorActivity.Path, Does.EndWith("Documents"));
                Assert.That(errorActivity.Details, Is.EqualTo(expectedMessage));
            });
        }


        [Test]
        public async Task StatusChanged_DeduplicatesUnchangedErrorActivityMessage()
        {
            Guid syncPairId = Guid.NewGuid();
            const string rawError = "There is not enough space on the disk.";
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(
                    enableNotifications: false,
                    CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", rawError),
            ]));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", rawError),
            ]));

            Assert.That(viewModel.Activities.Count(static activity => activity.Kind == "Error"), Is.EqualTo(1));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null),
            ]));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", rawError),
            ]));

            Assert.That(viewModel.Activities.Count(static activity => activity.Kind == "Error"), Is.EqualTo(2));
        }
    }
}
