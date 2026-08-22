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
        public async Task SaveSelectedSyncPairNameCommand_RejectsEmptyName()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.SelectedSyncPair!.EditableDisplayName = "   ";

            await ExecuteAsync(viewModel.SaveSelectedSyncPairNameCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RenamedSyncPairId, Is.Null);
                Assert.That(viewModel.SelectedSyncPair!.DisplayName, Is.EqualTo("Documents"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Sync folder name is required."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task SyncNowCommand_RetriesActionRequiredSyncAndClearsMessage()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Error")))
            {
                SyncAllStatus = new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null),
                ]),
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot("Server", false, "Cotton server not found."),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.SelfTestCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.CanRetryActionRequired, Is.True);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Cotton server not found."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });

            await ExecuteAsync(viewModel.SyncNowCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.SyncAllCalls, Is.EqualTo(1));
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Checked for changes"));
            });
        }


        [Test]
        public async Task ApproveRemoteMassDeleteCommand_TargetsExactGuardedPlanWithoutRetryLoop()
        {
            Guid syncPairId = Guid.NewGuid();
            const string rawError =
                "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100. "
                + "Plan fingerprint " + RemoteDeletePlanFingerprint + ".";
            const string expectedMessage =
                "Cotton Sync blocked a large remote delete plan (2207 pending deletes exceed limit 100). "
                + "Check local files and Cotton Cloud, then explicitly approve the exact delete plan.";
            DesktopSyncStatusSnapshot guardedStatus = new(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", rawError),
            ]);
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Music", "Error")))
            {
                SyncAllStatus = guardedStatus,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportStatus(guardedStatus);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.CanApproveRemoteMassDelete, Is.True);
                Assert.That(viewModel.RemoteMassDeleteApprovalText, Is.EqualTo("Approve 2,207 deletes"));
            });

            await ExecuteAsync(viewModel.ApproveRemoteMassDeleteCommand);
            await Task.Delay(50);

            Assert.Multiple(() =>
            {
                Assert.That(controller.SyncAllCalls, Is.EqualTo(1));
                Assert.That(controller.LastSyncAllPairId, Is.EqualTo(syncPairId));
                Assert.That(controller.LastApprovedRemoteDeletePlan, Is.EqualTo(
                    new RemoteDeletePlanApproval(2207, RemoteDeletePlanFingerprint)));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.CanApproveRemoteMassDelete, Is.True);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo(expectedMessage));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task StatusChanged_WithMultipleMassDeleteGuardsOffersNoAmbiguousAction()
        {
            Guid firstSyncPairId = Guid.NewGuid();
            Guid secondSyncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(firstSyncPairId, "Music", "Error"),
                    CreatePair(secondSyncPairId, "Photos", "Error")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    firstSyncPairId,
                    "Error",
                    "Remote delete blocked by mass-delete guard. 101 pending deletes exceed limit 100. "
                    + "Plan fingerprint " + RemoteDeletePlanFingerprint + "."),
                new DesktopSyncPairStatusSnapshot(
                    secondSyncPairId,
                    "Error",
                    "Remote delete blocked by mass-delete guard. 250 pending deletes exceed limit 100. "
                    + "Plan fingerprint " + RemoteDeletePlanFingerprint + "."),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.CanApproveRemoteMassDelete, Is.False);
                Assert.That(controller.SyncAllCalls, Is.Zero);
            });
        }


        [Test]
        public async Task Initialize_TreatsSyncPairErrorAsAttentionBeforeErrorMessageIsResolved()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Videos", "Error")));
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.HasStatusAttention, Is.True);
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Action required"));
                Assert.That(viewModel.IsStatusCardVisible, Is.True);
                Assert.That(viewModel.StatusCardTitle, Is.EqualTo("Sync needs attention"));
                Assert.That(viewModel.StatusCardDetailText, Is.EqualTo("Fix the folder issue to continue syncing."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the folder issue to continue syncing."));
            });
        }


        [Test]
        public async Task SelfTestPass_PreservesCurrentSyncPairErrorActionRequired()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Idle")))
            {
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot("Preferences database", true, "Ready"),
                    new DesktopSelfTestItemSnapshot("Sync state database", true, "Ready"),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Error", "There is not enough space on the disk."),
            ]));

            await ExecuteAsync(viewModel.SelfTestCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Action required"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("This computer does not have enough free disk space for sync. Free space and retry."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task CommandFailure_UpdatesProgressTextInsteadOfReportingUpToDate()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                SyncAllException = new CottonApiException(
                    HttpStatusCode.OK,
                    "<!doctype html><html>App</html>",
                    "Cotton API request GET /api/v1/sync/changes?since=0&limit=500 returned invalid JSON "
                    + "with content type 'text/html' and status 200 (OK)."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.SyncNowCommand.Execute(null);
            await WaitForAsync(() => viewModel.HasActionRequired);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
            });
        }


        [Test]
        public async Task CommandTransientServerFailure_ShowsOfflineWithoutActionRequired()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                SyncAllException = new AggregateException(
                    new CottonApiException(
                        HttpStatusCode.BadGateway,
                        "502 Bad Gateway",
                        "Cotton API request failed with status 502.")),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.SyncNowCommand.Execute(null);
            await WaitForAsync(() => string.Equals(viewModel.GlobalStatus, "Offline", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.CurrentProgressText, Is.Not.EqualTo("Fix the issue below to continue syncing."));
                Assert.That(
                    viewModel.Activities.First().Details,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
            });
        }
    }
}
