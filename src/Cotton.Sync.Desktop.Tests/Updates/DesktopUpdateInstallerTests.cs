// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Updates;
using System.Diagnostics;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public class DesktopUpdateInstallerTests
    {
        [Test]
        public void BuildSilentInstallArguments_RunsInstallerWithoutShowingWizard()
        {
            string arguments = DesktopUpdateInstaller.BuildSilentInstallArguments(launchAfterUpdate: true);

            Assert.Multiple(() =>
            {
                Assert.That(arguments, Does.Contain("/VERYSILENT"));
                Assert.That(arguments, Does.Contain("/SUPPRESSMSGBOXES"));
                Assert.That(arguments, Does.Contain("/NORESTART"));
                Assert.That(arguments, Does.Contain("/CLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/FORCECLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/LaunchAfterUpdate=1"));
            });
        }

        [Test]
        public void BuildSilentInstallArguments_CanStageInstallWithoutRelaunch()
        {
            string arguments = DesktopUpdateInstaller.BuildSilentInstallArguments(launchAfterUpdate: false);

            Assert.Multiple(() =>
            {
                Assert.That(arguments, Does.Contain("/VERYSILENT"));
                Assert.That(arguments, Does.Contain("/SUPPRESSMSGBOXES"));
                Assert.That(arguments, Does.Contain("/NORESTART"));
                Assert.That(arguments, Does.Contain("/CLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/FORCECLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Not.Contain("/LaunchAfterUpdate=1"));
            });
        }

        [Test]
        public void StartSilentInstall_VerifiesTrustedInstallerBeforeLaunch()
        {
            string installerPath = CreateUnsignedInstallerPath();
            try
            {
                FakeAuthenticodeVerifier verifier = new();
                FakeInstallerProcessLauncher launcher = new(new DesktopUpdateInstallResult(1234, false, null));
                DesktopUpdateInstaller installer = new(verifier, launcher);

                DesktopUpdateInstallResult result = installer.StartSilentInstall(installerPath, launchAfterUpdate: true);

                Assert.Multiple(() =>
                {
                    Assert.That(verifier.VerifiedPath, Is.EqualTo(installerPath));
                    Assert.That(launcher.StartCount, Is.EqualTo(1));
                    Assert.That(launcher.LastStartInfo?.FileName, Is.EqualTo(installerPath));
                    Assert.That(launcher.LastStartInfo?.UseShellExecute, Is.True);
                    Assert.That(launcher.LastStartInfo?.Arguments, Does.Contain("/VERYSILENT"));
                    Assert.That(launcher.LastStartInfo?.Arguments, Does.Contain("/LaunchAfterUpdate=1"));
                    Assert.That(result.ProcessId, Is.EqualTo(1234));
                });
            }
            finally
            {
                File.Delete(installerPath);
            }
        }

        [Test]
        public void StartSilentInstall_DoesNotLaunchWhenTrustVerificationFails()
        {
            string installerPath = CreateUnsignedInstallerPath();
            try
            {
                InvalidDataException failure = new("signature rejected");
                FakeAuthenticodeVerifier verifier = new(failure);
                FakeInstallerProcessLauncher launcher = new(new DesktopUpdateInstallResult(1234, false, null));
                DesktopUpdateInstaller installer = new(verifier, launcher);

                InvalidDataException? exception = Assert.Throws<InvalidDataException>(() =>
                    installer.StartSilentInstall(installerPath, launchAfterUpdate: true));

                Assert.Multiple(() =>
                {
                    Assert.That(exception, Is.SameAs(failure));
                    Assert.That(verifier.VerifiedPath, Is.EqualTo(installerPath));
                    Assert.That(launcher.StartCount, Is.EqualTo(0));
                });
            }
            finally
            {
                File.Delete(installerPath);
            }
        }

        [Test]
        [Platform(Include = "Win")]
        public void WindowsAuthenticodeUpdateVerifier_RejectsUnsignedInstallerFile()
        {
            string installerPath = CreateUnsignedInstallerPath();
            try
            {
                WindowsAuthenticodeUpdateVerifier verifier = new();

                InvalidDataException? exception = Assert.Throws<InvalidDataException>(() =>
                    verifier.VerifyTrustedInstaller(installerPath));

                Assert.That(exception?.Message, Does.Contain("not signed by a trusted publisher"));
            }
            finally
            {
                File.Delete(installerPath);
            }
        }

        private static string CreateUnsignedInstallerPath()
        {
            string installerPath = Path.Combine(
                Path.GetTempPath(),
                "cotton-update-installer-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(installerPath, "unsigned");
            return installerPath;
        }

        private class FakeAuthenticodeVerifier : IDesktopUpdateAuthenticodeVerifier
        {
            private readonly Exception? _failure;

            public FakeAuthenticodeVerifier(Exception? failure = null)
            {
                _failure = failure;
            }

            public string? VerifiedPath { get; private set; }

            public void VerifyTrustedInstaller(string installerPath)
            {
                VerifiedPath = installerPath;
                if (_failure is not null)
                {
                    throw _failure;
                }
            }
        }

        private class FakeInstallerProcessLauncher : IDesktopUpdateInstallerProcessLauncher
        {
            private readonly DesktopUpdateInstallResult _result;

            public FakeInstallerProcessLauncher(DesktopUpdateInstallResult result)
            {
                _result = result;
            }

            public int StartCount { get; private set; }

            public ProcessStartInfo? LastStartInfo { get; private set; }

            public DesktopUpdateInstallResult Start(ProcessStartInfo startInfo)
            {
                StartCount++;
                LastStartInfo = startInfo;
                return _result;
            }
        }
    }
}
