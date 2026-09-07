// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopStartupOptionsTests
    {
        [Test]
        public void Parse_LeavesLiveSoakDisabledByDefault()
        {
            Assert.That(DesktopStartupOptions.Parse(["--live-sync-smoke"]).LiveSyncSmokeSoakDuration, Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void Parse_LoadsExplicitLiveSoakDuration()
        {
            Assert.That(DesktopStartupOptions.Parse(["--live-sync-smoke", "--live-sync-smoke-soak-seconds", "7200"]).LiveSyncSmokeSoakDuration,
                Is.EqualTo(TimeSpan.FromHours(2)));
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("bad")]
        public void Parse_RejectsInvalidLiveSoakDuration(string duration)
        {
            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(["--live-sync-smoke", "--live-sync-smoke-soak-seconds", duration]));
        }

        [Test]
        public void Parse_RejectsLiveSoakWithoutLiveRun()
        {
            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(["--self-test", "--live-sync-smoke-soak-seconds", "60"]));
        }
    }
}
