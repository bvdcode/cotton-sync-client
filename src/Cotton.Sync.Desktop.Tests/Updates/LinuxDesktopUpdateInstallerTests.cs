// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public class LinuxDesktopUpdateInstallerTests
    {
        [Test]
        public void CreateInstallStartInfo_RequestsAuthorizationAndPreservesPackagePath()
        {
            string packagePath = Path.GetFullPath(Path.Combine("updates with spaces", "CottonSync-Linux.deb"));
            ProcessStartInfo startInfo = LinuxDesktopUpdateInstaller.CreateInstallStartInfo(packagePath);

            Assert.Multiple(() =>
            {
                Assert.That(startInfo.FileName, Is.EqualTo("/usr/bin/pkexec"));
                Assert.That(startInfo.UseShellExecute, Is.False);
                Assert.That(startInfo.ArgumentList, Is.EqualTo(new[]
                {
                    "/usr/bin/pkcon", "--noninteractive", "--plain", "install-local", packagePath,
                }));
            });
        }
    }
}
