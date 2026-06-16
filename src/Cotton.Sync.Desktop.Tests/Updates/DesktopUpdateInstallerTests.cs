// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Updates;

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
                Assert.That(arguments, Does.Not.Contain("/LaunchAfterUpdate=1"));
            });
        }
    }
}
