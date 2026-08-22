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
        public void FromException_ExplainsHtmlApiResponse()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.OK,
                "<!doctype html><html>App</html>",
                "Cotton API request GET /api/v1/settings returned invalid JSON with content type 'text/html' and status 200 (OK).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton API returned a web page instead of JSON. Check the server URL or backend deployment and retry."));
        }

        [Test]
        public void FromException_ExplainsRawJsonParserHtmlStartMessage()
        {
            InvalidOperationException exception = new InvalidOperationException(
                "'<' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton API returned a web page instead of JSON. Check the server URL or backend deployment and retry."));
        }

        [Test]
        public void FromException_ExplainsLocalPermissionDeniedException()
        {
            LocalFilePermissionDeniedException exception = new LocalFilePermissionDeniedException(
                "Locked/report.docx",
                "/home/qa/Cotton/Locked/report.docx",
                "owner does not have read permission");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton Sync cannot access 'Locked/report.docx'. Grant file permissions and retry sync."));
        }

        [Test]
        public void FromException_ExplainsDiskFullException()
        {
            IOException exception = new IOException("No space left on device");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("This computer does not have enough free disk space for sync. Free space and retry."));
        }

        [Test]
        public void FromException_ExplainsLocalFileUnavailableException()
        {
            LocalFileUnavailableException exception = new LocalFileUnavailableException(
                "Drafts/report.docx",
                "/home/qa/Cotton/Drafts/report.docx",
                "the file changed during scanning.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton Sync cannot read 'Drafts/report.docx' yet. Close the app using it or wait for it to finish saving, then retry sync."));
        }

        [Test]
        public void FromException_ExplainsMissingLocalSyncFolder()
        {
            DirectoryNotFoundException exception = new DirectoryNotFoundException(
                "Local root does not exist: C:\\Users\\QA\\Cotton.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync."));
        }

        [Test]
        public void FromException_ExplainsRemoteQuotaExceeded()
        {
            CottonApiException exception = new CottonApiException(
                (HttpStatusCode)507,
                null,
                "Cotton API request failed with status 507.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Remote storage quota exceeded. Free space in Cotton Cloud or choose a smaller sync folder."));
        }

        [Test]
        public void FromException_ExplainsRemoteUploadTooLarge()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.RequestEntityTooLarge,
                null,
                "Cotton API request failed with status 413.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Remote upload was rejected because it is larger than the server limit."));
        }

        [Test]
        public void FromException_ExplainsMissingSyncStateTable()
        {
            InvalidOperationException exception = new InvalidOperationException("SQLite Error 1: 'no such table: sync_entries'.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Local sync state database is unavailable. Run diagnostics and restart Cotton Sync."));
        }

        [Test]
        public void FromException_ExplainsCorruptLocalStateDatabase()
        {
            InvalidOperationException exception = new InvalidOperationException("SQLite Error 26: 'file is not a database'.");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Local Cotton Sync state appears to be corrupt. Export diagnostics, then reset the local app data or choose a fresh data directory and sign in again."));
        }

        [Test]
        public void FromException_ExplainsCloudFilesSyncRootConnectionFailure()
        {
            WindowsCloudFilesNativeException exception = new WindowsCloudFilesNativeException("CfConnectSyncRoot", unchecked((int)0x8007017C));

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Windows virtual files could not connect this sync folder to File Explorer. Restart Cotton Sync, then export diagnostics if it repeats."));
        }

        [Test]
        public void FromException_ExplainsCloudFilesPlaceholderFailure()
        {
            WindowsCloudFilesNativeException exception = new WindowsCloudFilesNativeException("CfCreatePlaceholders", unchecked((int)0x8007017C));

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(
                message,
                Is.EqualTo("Windows virtual files could not make a cloud file available in File Explorer. Check diagnostics and retry sync."));
        }

        [Test]
        public void FromException_UsesHumanTotpRequiredMessage()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Two-factor authentication code is required\"}",
                "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Enter the 2FA code for this account."));
        }

        [Test]
        public void FromException_UsesHumanInvalidCredentialsMessage()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Unauthorized,
                "{\"success\":false,\"message\":\"User not found\"}",
                "Cotton API request POST /api/v1/auth/login failed with status 401 (Unauthorized).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Invalid username or password."));
        }

        [Test]
        public void FromException_UsesHumanInvalidPasswordMessageForForbiddenServerResponse()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Invalid password\"}",
                "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Invalid username or password."));
        }

        [Test]
        public void FromException_UsesHumanInvalidTotpMessage()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Invalid two-factor authentication code\"}",
                "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Invalid 2FA code."));
        }

        [Test]
        public void FromException_UsesHumanTotpLockoutMessage()
        {
            CottonApiException exception = new CottonApiException(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Maximum number of TOTP verification attempts exceeded\"}",
                "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden).");

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Too many invalid 2FA attempts. Try again later or sign in from the web app."));
        }

        [Test]
        public void FromException_UsesReadableFallbackWhenExceptionHasNoMessage()
        {
            InvalidOperationException exception = new InvalidOperationException(string.Empty);

            string message = DesktopActionRequiredMessageResolver.FromException(exception);

            Assert.That(message, Is.EqualTo("Operation could not be completed. Check diagnostics and retry."));
        }
    }
}
