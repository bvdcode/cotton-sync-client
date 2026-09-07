// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopStartupOptionsTests
    {
        [TestCase("--live-sync-smoke")]
        [TestCase("--desktop-live-sync-smoke")]
        public void Parse_LoadsExplicitLivePasswordEnvironmentVariable(string liveOption)
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(
                [liveOption, "--username", " demo-user ", "--live-sync-smoke-password-env= COTTON_TEST_PASSWORD "]);

            Assert.Multiple(() =>
            {
                Assert.That(options.RunLiveSyncSmoke, Is.True);
                Assert.That(options.Username, Is.EqualTo("demo-user"));
                Assert.That(options.LiveSyncSmokePasswordEnvironmentVariable, Is.EqualTo("COTTON_TEST_PASSWORD"));
            });
        }

        [Test]
        public void Parse_LeavesLiveBrowserSignInSelectedWithoutPasswordOption()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--live-sync-smoke", "--username", "demo-user"]);

            Assert.That(options.LiveSyncSmokePasswordEnvironmentVariable, Is.Null);
        }

        [Test]
        public void Parse_RejectsPasswordEnvironmentVariableOutsideLiveSmoke()
        {
            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(
                ["--self-test", "--username", "demo-user", "--live-sync-smoke-password-env", "COTTON_TEST_PASSWORD"]));
        }

        [TestCase(null)]
        [TestCase(" ")]
        public void Parse_RejectsPasswordEnvironmentVariableWithoutUsername(string? username)
        {
            List<string> arguments = ["--live-sync-smoke", "--live-sync-smoke-password-env", "COTTON_TEST_PASSWORD"];
            if (username is not null)
            {
                arguments.AddRange(["--username", username]);
            }

            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(arguments));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("NAME=VALUE")]
        [TestCase("NAME\0VALUE")]
        public void Parse_RejectsInvalidPasswordEnvironmentVariableName(string value)
        {
            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(
                ["--live-sync-smoke", "--username", "demo-user", "--live-sync-smoke-password-env=" + value]));
        }

        [Test]
        public void Parse_RejectsPasswordEnvironmentVariableWithMissingValue()
        {
            Assert.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(
                ["--live-sync-smoke", "--username", "demo-user", "--live-sync-smoke-password-env", "--data-dir", "state"]));
        }
    }
}
