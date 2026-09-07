// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopCommandLineRunnerTests
    {
        [TestCase(null)]
        [TestCase(" ")]
        public async Task RunLiveSyncSmokeAsync_RejectsMissingPasswordBeforeCreatingRoots(string? password)
        {
            string variableName = "COTTON_TEST_PASSWORD_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variableName, password);
            string data = Path.Combine(_tempDirectory, "state");
            string first = Path.Combine(_tempDirectory, "first");
            string second = Path.Combine(_tempDirectory, "second");
            try
            {
                DesktopStartupOptions options = DesktopStartupOptions.Parse(
                    ["--live-sync-smoke", "--server", "https://cotton.example.test/", "--username", "demo-user",
                        "--data-dir", data, "--local-root", first, "--second-local-root", second,
                        "--remote-path", "/Smoke", "--live-sync-smoke-password-env", variableName]);
                using StringWriter output = new();

                int exitCode = await DesktopCommandLineRunner.RunLiveSyncSmokeAsync(options, output);

                Assert.Multiple(() =>
                {
                    Assert.That(exitCode, Is.EqualTo(2));
                    Assert.That(output.ToString(), Does.Contain("password environment variable is missing or empty"));
                    Assert.That(output.ToString(), Does.Not.Contain(variableName));
                    Assert.That(Directory.Exists(data), Is.False);
                    Assert.That(Directory.Exists(first), Is.False);
                    Assert.That(Directory.Exists(second), Is.False);
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }
    }
}
