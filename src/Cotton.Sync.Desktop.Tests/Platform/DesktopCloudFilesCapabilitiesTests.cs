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
                if (OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
                {
                    Assert.That(snapshot.WindowsVirtualFilesDetails, Does.Contain("Cloud Files API"));
                    if (snapshot.WindowsVirtualFilesDetails.Contains("shell helper", StringComparison.Ordinal)
                        || snapshot.WindowsVirtualFilesDetails.Contains("StorageProvider", StringComparison.Ordinal))
                    {
                        Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.False);
                    }
                    else
                    {
                        Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.True);
                    }
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
