// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class DesktopPlatformCapabilitiesTests
    {
        [Test]
        public void CreateSnapshot_MatchesPlatformCapabilityFlags()
        {
            DesktopPlatformCapabilitySnapshot snapshot = DesktopPlatformCapabilities.CreateSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.OperatingSystemName, Is.Not.Empty);
                Assert.That(snapshot.DesktopSession, Is.Not.Empty);
                Assert.That(snapshot.CurrentDesktop, Is.Not.Empty);
                Assert.That(snapshot.IsAutostartSupported, Is.EqualTo(DesktopPlatformCapabilities.IsAutostartSupported));
                Assert.That(snapshot.IsTrayLifecycleSupported, Is.EqualTo(DesktopPlatformCapabilities.IsTrayLifecycleSupported));
                Assert.That(snapshot.TrayLifecycleDetails, Is.Not.Empty);
            });
        }

        [Test]
        public async Task CreateSnapshot_OnLinux_ReportsActualTrayHostAvailability()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("Requires a Linux desktop session.");
            }

            await DesktopPlatformCapabilities.InitializeAsync();
            bool hostAvailable = await LinuxTrayHostProbe.IsAvailableAsync();
            DesktopPlatformCapabilitySnapshot snapshot = DesktopPlatformCapabilities.CreateSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.OperatingSystemName, Is.EqualTo("Linux"));
                Assert.That(snapshot.IsTrayLifecycleSupported, Is.EqualTo(hostAvailable));
                Assert.That(snapshot.TrayLifecycleDetails, Does.Contain("StatusNotifier"));
            });
        }
    }
}
