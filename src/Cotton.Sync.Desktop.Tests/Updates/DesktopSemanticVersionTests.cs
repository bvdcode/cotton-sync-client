// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public class DesktopSemanticVersionTests
    {
        [Test]
        public void Parse_StripsBuildMetadata()
        {
            DesktopSemanticVersion version = DesktopSemanticVersion.Parse("0.0.1+4013523");

            Assert.That(version.ToString(), Is.EqualTo("0.0.1"));
        }

        [Test]
        public void CompareTo_TreatsStableReleaseAsNewerThanPrerelease()
        {
            DesktopSemanticVersion prerelease = DesktopSemanticVersion.Parse("0.0.1-dev");
            DesktopSemanticVersion stable = DesktopSemanticVersion.Parse("0.0.1");

            Assert.That(stable.CompareTo(prerelease), Is.GreaterThan(0));
        }

        [Test]
        public void CompareTo_OrdersPatchVersions()
        {
            DesktopSemanticVersion current = DesktopSemanticVersion.Parse("0.0.1");
            DesktopSemanticVersion latest = DesktopSemanticVersion.Parse("0.0.2");

            Assert.That(latest.CompareTo(current), Is.GreaterThan(0));
        }

        [Test]
        public void Parse_RejectsIncompleteVersion()
        {
            Assert.Throws<FormatException>(() => DesktopSemanticVersion.Parse("0.0"));
        }
    }
}
