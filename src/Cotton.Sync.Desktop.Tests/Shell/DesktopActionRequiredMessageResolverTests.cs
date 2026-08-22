// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sdk;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopActionRequiredMessageResolverTests
    {
        [Test]
        public void FromStatus_ReturnsFirstPairError()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Idle", null),
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Error", "Remote folder is unavailable."),
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Error", "Local folder is unavailable."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(message, Is.EqualTo("Remote folder is unavailable."));
        }

        [Test]
        public void FromStatus_ExplainsRemoteMassDeleteGuard()
        {
            DesktopSyncStatusSnapshot status = new(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo(
                    "Cotton Sync blocked a large remote delete plan (2207 pending deletes exceed limit 100). "
                    + "Check local files and Cotton Cloud, then explicitly approve the exact delete plan."));
        }

        [Test]
        public void FromException_ExplainsRemoteMassDeleteGuard()
        {
            InvalidOperationException exception = new(
                "Remote delete blocked by mass-delete guard. 42 pending deletes exceed limit 10.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo(
                    "Cotton Sync blocked a large remote delete plan (42 pending deletes exceed limit 10). "
                    + "Check local files and Cotton Cloud, then explicitly approve the exact delete plan."));
        }

        [TestCase(
            "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100.",
            2207)]
        [TestCase(
            "Cotton Sync blocked a large remote delete plan (42 pending deletes exceed limit 10). Check local files.",
            42)]
        public void TryGetRemoteMassDeleteCount_ParsesGuardedPlan(string message, int expectedCount)
        {
            bool parsed = DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteCount(message, out int deleteCount);

            Assert.Multiple(() =>
            {
                Assert.That(parsed, Is.True);
                Assert.That(deleteCount, Is.EqualTo(expectedCount));
            });
        }

        [TestCase("101 pending deletes exceed limit 100.")]
        [TestCase("Remote delete blocked by mass-delete guard. pending deletes exceed limit 100.")]
        [TestCase("Remote delete blocked by mass-delete guard. 0 pending deletes exceed limit 100.")]
        public void TryGetRemoteMassDeleteCount_RejectsUnapprovedMessages(string message)
        {
            bool parsed = DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteCount(message, out int deleteCount);

            Assert.Multiple(() =>
            {
                Assert.That(parsed, Is.False);
                Assert.That(deleteCount, Is.Zero);
            });
        }

        [Test]
        public void TryGetRemoteMassDeleteApproval_RequiresExactFingerprint()
        {
            const string fingerprint =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string approvedMessage =
                "Remote delete blocked by mass-delete guard. 101 pending deletes exceed limit 100. "
                + "Plan fingerprint " + fingerprint + ".";
            const string legacyMessage =
                "Remote delete blocked by mass-delete guard. 101 pending deletes exceed limit 100.";

            bool parsed = DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteApproval(
                approvedMessage,
                out RemoteDeletePlanApproval approval);
            bool parsedLegacy = DesktopActionRequiredMessageResolver.TryGetRemoteMassDeleteApproval(
                legacyMessage,
                out RemoteDeletePlanApproval legacyApproval);

            Assert.Multiple(() =>
            {
                Assert.That(parsed, Is.True);
                Assert.That(approval, Is.EqualTo(new RemoteDeletePlanApproval(101, fingerprint)));
                Assert.That(parsedLegacy, Is.False);
                Assert.That(legacyApproval, Is.Null);
            });
        }

        [Test]
        public void FromStatus_ExplainsMissingDesktopSyncChangesApi()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "Cotton API request GET /api/v1/sync/changes?since=0&limit=500 returned invalid JSON "
                    + "with content type 'text/html' and status 200 (OK). Response: <!doctype html><html>App</html>"),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
        }

        [Test]
        public void FromException_ExplainsTemporaryChangeFeedRoute404()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.NotFound,
                "404 page not found",
                "Cotton API request GET /api/v1/sync/changes?since=9828&limit=500 failed with status 404 (NotFound).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
        }

        [Test]
        public void FromException_ExplainsTemporarilyLockedServer()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Locked,
                "{\"locked\":true,\"message\":\"Cotton is locked until the master key is provided.\"}",
                "Cotton API request GET /api/v1/auth/me failed with status 423 (Locked).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton Cloud reports that the server is locked. Unlock it in the web app; Cotton Sync will retry automatically."));
        }

        [Test]
        public void FromStatus_ExplainsRawJsonParserHtmlStartMessage()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "'<' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Cotton API returned a web page instead of JSON. Check the server URL or backend deployment and retry."));
        }

        [Test]
        public void FromStatus_ExplainsLocalPermissionDeniedMessage()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "Local file 'Locked/report.docx' cannot be read because permission was denied."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Cotton Sync cannot access 'Locked/report.docx'. Grant file permissions and retry sync."));
        }

        [Test]
        public void FromStatus_ExplainsDiskFullMessage()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Error", "There is not enough space on the disk."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("This computer does not have enough free disk space for sync. Free space and retry."));
        }

        [Test]
        public void FromStatus_ExplainsLocalFileUnavailableMessage()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "Local file 'Drafts/report.docx' could not be scanned safely."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Cotton Sync cannot read 'Drafts/report.docx' yet. Close the app using it or wait for it to finish saving, then retry sync."));
        }

        [Test]
        public void FromStatus_ExplainsMissingSyncStateTable()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "SQLite Error 1: 'no such table: sync_change_cursors'."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Local sync state database is unavailable. Run diagnostics and restart Cotton Sync."));
        }

        [Test]
        public void FromStatus_ExplainsCorruptLocalStateDatabase()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "SQLite Error 26: 'file is not a database'."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Local Cotton Sync state appears to be corrupt. Export diagnostics, then reset the local app data or choose a fresh data directory and sign in again."));
        }

        [Test]
        public void FromStatus_PreservesWindowsVirtualFilesPlaceholderDeleteOrRenameAction()
        {
            const string expected =
                "An online-only file was deleted or moved locally. Restore it from Cotton Sync or delete/rename it from Cotton web before syncing.";
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Error", expected),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(message, Is.EqualTo(expected));
        }

        [Test]
        public void FromStatus_ExplainsCloudFilesSyncRootRegistrationFailure()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "CfRegisterSyncRoot failed with HRESULT 0x8007017C."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Windows virtual files could not register this sync folder with File Explorer. Restart Cotton Sync, then export diagnostics if it repeats."));
        }

        [Test]
        public void FromStatus_ExplainsCloudFilesPlaceholderFailure()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "CfCreatePlaceholders failed with HRESULT 0x8007017C."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Windows virtual files could not make a cloud file available in File Explorer. Check diagnostics and retry sync."));
        }

        [Test]
        public void FromStatus_NormalizesEmbeddedUploadBadRequestProblemDetails()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    Guid.NewGuid(),
                    "Error",
                    "Cotton API request POST /api/v1/files/from-chunks failed with status 400 (BadRequest). "
                    + "Response: {\"type\":\"https://tools.ietf.org/html/rfc7231#section-6.5.1\","
                    + "\"title\":\"Bad Request\",\"status\":400,\"detail\":\"Bad request\","
                    + "\"instance\":\"/api/v1/files/from-chunks\",\"traceId\":\"00-test\"}"),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("Remote upload was rejected by Cotton Cloud. Check diagnostics and retry."));
        }

        [Test]
        public void FromStatus_ReturnsEmptyWhenNoPairHasError()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Idle", null),
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Syncing", string.Empty),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(message, Is.Empty);
        }

        [Test]
        public void FromStatus_ReturnsEmptyForOfflinePairDetails()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Offline", "Cannot reach Cotton Cloud."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(message, Is.Empty);
        }

        [Test]
        public void FromStatus_ReturnsGenericMessageWhenPairErrorHasNoDetails()
        {
            DesktopSyncStatusSnapshot status = new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(Guid.NewGuid(), "Error", null),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);

            Assert.That(
                message,
                Is.EqualTo("One or more sync folders reported an error. Check diagnostics and retry."));
        }

        [Test]
        public void FromSelfTest_ReturnsFirstFailedCheckDetails()
        {
            DesktopSelfTestSnapshot result = new DesktopSelfTestSnapshot(
            [
                new DesktopSelfTestItemSnapshot("Database", true, "Ready"),
                new DesktopSelfTestItemSnapshot("Server", false, "Cotton server not found."),
                new DesktopSelfTestItemSnapshot("Local root", false, "Missing folder."),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromSelfTest(result);

            Assert.That(message, Is.EqualTo("Cotton server not found."));
        }

        [Test]
        public void FromSelfTest_ReturnsEmptyWhenSelfTestPassed()
        {
            DesktopSelfTestSnapshot result = new DesktopSelfTestSnapshot(
            [
                new DesktopSelfTestItemSnapshot("Database", true, "Ready"),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromSelfTest(result);

            Assert.That(message, Is.Empty);
        }

        [Test]
        public void FromSelfTest_ReturnsEmptyWhenSelfTestOnlySkippedChecks()
        {
            DesktopSelfTestSnapshot result = new DesktopSelfTestSnapshot(
            [
                new DesktopSelfTestItemSnapshot("Desktop sync change feed", false, "Sign in to verify", Skipped: true),
            ]);

            string message = DesktopActionRequiredMessageResolver.FromSelfTest(result);

            Assert.That(message, Is.Empty);
        }

    }
}
