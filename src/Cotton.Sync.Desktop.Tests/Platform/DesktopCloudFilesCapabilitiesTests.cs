// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class DesktopCloudFilesCapabilitiesTests
    {
        [Test]
        public void CreateSyncPairModeCapabilities_ReportsCurrentHostSupportWithoutThrowing()
        {
            var snapshot = DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.WindowsVirtualFilesDetails, Is.Not.Empty);
                if (OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
                {
                    Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.False);
                    Assert.That(
                        snapshot.WindowsVirtualFilesDetails,
                        Does.Contain("Cloud Files API")
                            .Or.Contain("Free up space"));
                    Assert.That(
                        snapshot.WindowsVirtualFilesDetails,
                        Does.Contain("shell helper")
                            .Or.Contain("StorageProvider")
                            .Or.Contain("Free up space"));
                }
                else
                {
                    Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.False);
                    Assert.That(snapshot.WindowsVirtualFilesDetails, Does.Contain("Windows"));
                }
            });
        }
    }
}
