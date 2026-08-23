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
            string dataDirectory = Path.Combine(Path.GetTempPath(), "Cotton profile");
            string arguments = DesktopUpdateInstaller.BuildSilentInstallArguments(
                launchAfterUpdate: true,
                dataDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(arguments, Does.Contain("/VERYSILENT"));
                Assert.That(arguments, Does.Contain("/SUPPRESSMSGBOXES"));
                Assert.That(arguments, Does.Contain("/NORESTART"));
                Assert.That(arguments, Does.Contain("/CLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/FORCECLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/LaunchAfterUpdate=1"));
                Assert.That(arguments, Does.Contain("/LaunchAfterUpdateDataDir=\"" + dataDirectory + "\""));
            });
        }

        [Test]
        public void BuildSilentInstallArguments_CanStageInstallWithoutRelaunch()
        {
            string arguments = DesktopUpdateInstaller.BuildSilentInstallArguments(
                launchAfterUpdate: false,
                Path.GetTempPath());

            Assert.Multiple(() =>
            {
                Assert.That(arguments, Does.Contain("/VERYSILENT"));
                Assert.That(arguments, Does.Contain("/SUPPRESSMSGBOXES"));
                Assert.That(arguments, Does.Contain("/NORESTART"));
                Assert.That(arguments, Does.Contain("/CLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Contain("/FORCECLOSEAPPLICATIONS"));
                Assert.That(arguments, Does.Not.Contain("/LaunchAfterUpdate=1"));
                Assert.That(arguments, Does.Not.Contain("/LaunchAfterUpdateDataDir="));
            });
        }

        [Test]
        public void StartSilentInstall_LaunchesInstaller()
        {
            string installerPath = CreateUnsignedInstallerPath();
            try
            {
                FakeInstallerProcessLauncher launcher = new(new DesktopUpdateInstallResult(1234, false, null));
                string dataDirectory = Path.Combine(Path.GetTempPath(), "Cotton profile");
                DesktopUpdateInstaller installer = new(launcher, dataDirectory);

                DesktopUpdateInstallResult result = installer.StartSilentInstall(installerPath, launchAfterUpdate: true);

                Assert.Multiple(() =>
                {
                    Assert.That(launcher.StartCount, Is.EqualTo(1));
                    Assert.That(launcher.LastStartInfo?.FileName, Is.EqualTo(installerPath));
                    Assert.That(launcher.LastStartInfo?.UseShellExecute, Is.True);
                    Assert.That(launcher.LastStartInfo?.Arguments, Does.Contain("/VERYSILENT"));
                    Assert.That(launcher.LastStartInfo?.Arguments, Does.Contain("/LaunchAfterUpdate=1"));
                    Assert.That(
                        launcher.LastStartInfo?.Arguments,
                        Does.Contain("/LaunchAfterUpdateDataDir=\"" + dataDirectory + "\""));
                    Assert.That(result.ProcessId, Is.EqualTo(1234));
                });
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
