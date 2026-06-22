// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public class DesktopPendingUpdateStartupTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-pending-update-tests-" + Guid.NewGuid().ToString("N"));
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
        public void TryStartPendingUpdate_StartsValidNewerInstallerAndIncrementsAttemptCount()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            string installerPath = WriteInstaller(paths, installerBytes);
            var store = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory);
            store.Save(new DesktopPendingUpdate(
                "0.0.2",
                installerPath,
                Sha256(installerBytes),
                installerBytes.Length,
                DateTime.UtcNow));
            var installer = new FakeUpdateInstaller();

            bool started = DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, "0.0.1", installer);

            DesktopPendingUpdate? saved = store.TryLoad();
            Assert.Multiple(() =>
            {
                Assert.That(started, Is.True);
                Assert.That(installer.InstallerPath, Is.EqualTo(installerPath));
                Assert.That(installer.LaunchAfterUpdate, Is.True);
                Assert.That(saved?.AttemptCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryStartPendingUpdate_DeletesMarkerWhenVersionIsNotNewer()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v1");
            string installerPath = WriteInstaller(paths, installerBytes);
            var store = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory);
            store.Save(new DesktopPendingUpdate(
                "0.0.1",
                installerPath,
                Sha256(installerBytes),
                installerBytes.Length,
                DateTime.UtcNow));
            var installer = new FakeUpdateInstaller();

            bool started = DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, "0.0.1", installer);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.False);
                Assert.That(installer.InstallerPath, Is.Null);
                Assert.That(store.TryLoad(), Is.Null);
            });
        }

        [Test]
        public void TryStartPendingUpdate_DeletesMarkerWhenInstallerHashDoesNotMatch()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            string installerPath = WriteInstaller(paths, installerBytes);
            var store = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory);
            store.Save(new DesktopPendingUpdate(
                "0.0.2",
                installerPath,
                new string('b', 64),
                installerBytes.Length,
                DateTime.UtcNow));
            var installer = new FakeUpdateInstaller();

            bool started = DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, "0.0.1", installer);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.False);
                Assert.That(installer.InstallerPath, Is.Null);
                Assert.That(store.TryLoad(), Is.Null);
            });
        }

        [Test]
        public void TryStartPendingUpdate_DeletesMarkerAfterTooManyStartupAttempts()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            string installerPath = WriteInstaller(paths, installerBytes);
            var store = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory);
            store.Save(new DesktopPendingUpdate(
                "0.0.2",
                installerPath,
                Sha256(installerBytes),
                installerBytes.Length,
                DateTime.UtcNow,
                AttemptCount: 3));
            var installer = new FakeUpdateInstaller();

            bool started = DesktopPendingUpdateStartup.TryStartPendingUpdate(paths, "0.0.1", installer);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.False);
                Assert.That(installer.InstallerPath, Is.Null);
                Assert.That(store.TryLoad(), Is.Null);
            });
        }

        private static string WriteInstaller(DesktopAppPaths paths, byte[] installerBytes)
        {
            string directory = Path.Combine(paths.UpdateCacheDirectory, "0.0.2");
            Directory.CreateDirectory(directory);
            string installerPath = Path.Combine(directory, "CottonSync-Windows-Setup.exe");
            File.WriteAllBytes(installerPath, installerBytes);
            return installerPath;
        }

        private static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private sealed class FakeUpdateInstaller : IDesktopUpdateInstaller
        {
            public string? InstallerPath { get; private set; }

            public bool? LaunchAfterUpdate { get; private set; }

            public DesktopUpdateInstallResult StartSilentInstall(
                string installerPath,
                bool launchAfterUpdate)
            {
                InstallerPath = installerPath;
                LaunchAfterUpdate = launchAfterUpdate;
                return new DesktopUpdateInstallResult(42, false, null);
            }
        }
    }
}
