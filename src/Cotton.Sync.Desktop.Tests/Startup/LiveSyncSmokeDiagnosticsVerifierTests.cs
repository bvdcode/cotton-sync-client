// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Text.Json;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class LiveSyncSmokeDiagnosticsVerifierTests
    {
        private string _tempDirectory = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-live-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void Verify_AcceptsSignedInIdlePublicBundle()
        {
            Guid syncPairId = Guid.NewGuid();
            string archivePath = CreateArchive(syncPairId, accountName: "Signed in", isSignedIn: true, status: "Idle");

            LiveSyncSmokeDiagnosticsVerification result =
                LiveSyncSmokeDiagnosticsVerifier.Verify(archivePath, syncPairId);

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True);
                Assert.That(result.Details, Does.Contain("pairStatus=Idle"));
                Assert.That(result.Details, Does.Contain("privateEntry=False"));
            });
        }

        [Test]
        public void Verify_RejectsSignedOutBundle()
        {
            Guid syncPairId = Guid.NewGuid();
            string archivePath = CreateArchive(syncPairId, accountName: "Signed out", isSignedIn: false, status: "Idle");

            LiveSyncSmokeDiagnosticsVerification result =
                LiveSyncSmokeDiagnosticsVerifier.Verify(archivePath, syncPairId);

            Assert.That(result.Passed, Is.False);
        }

        [Test]
        public void Verify_RejectsPrivateDatabaseEntry()
        {
            Guid syncPairId = Guid.NewGuid();
            string archivePath = CreateArchive(
                syncPairId,
                accountName: "Signed in",
                isSignedIn: true,
                status: "Idle",
                privateEntryName: "state/sync-state.db");

            LiveSyncSmokeDiagnosticsVerification result =
                LiveSyncSmokeDiagnosticsVerifier.Verify(archivePath, syncPairId);

            Assert.That(result.Passed, Is.False);
        }

        private string CreateArchive(
            Guid syncPairId,
            string accountName,
            bool isSignedIn,
            string status,
            string? privateEntryName = null)
        {
            string archivePath = Path.Combine(_tempDirectory, "diagnostics.zip");
            using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            ZipArchiveEntry diagnosticsEntry = archive.CreateEntry("diagnostics.json");
            using (Stream stream = diagnosticsEntry.Open())
            {
                JsonSerializer.Serialize(
                    stream,
                    new
                    {
                        appVersion = "0.1.0",
                        accountName,
                        syncLifecycle = new { isSignedIn },
                        syncPairs = new[]
                        {
                            new
                            {
                                id = syncPairId,
                                status,
                            },
                        },
                    });
            }

            archive.CreateEntry("logs/cotton-sync.log");
            if (privateEntryName is not null)
            {
                archive.CreateEntry(privateEntryName);
            }

            return archivePath;
        }
    }
}
