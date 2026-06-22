// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Diagnostics;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsClipboardServiceTests
    {
        [Test]
        public void CreateStartInfo_UsesEnvironmentValueForClipboardText()
        {
            ProcessStartInfo startInfo = WindowsClipboardService.CreateStartInfo(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                "https://cloud.example/s/generated-token");
            string command = WindowsClipboardService.DecodePowerShellCommand(startInfo.ArgumentList.Last());

            Assert.Multiple(() =>
            {
                Assert.That(startInfo.UseShellExecute, Is.False);
                Assert.That(startInfo.CreateNoWindow, Is.True);
                Assert.That(startInfo.RedirectStandardError, Is.True);
                Assert.That(startInfo.ArgumentList, Does.Contain("-NoProfile"));
                Assert.That(startInfo.ArgumentList, Does.Contain("-NonInteractive"));
                Assert.That(command, Does.Contain("Set-Clipboard -Value $env:COTTON_SYNC_CLIPBOARD_TEXT"));
                Assert.That(startInfo.Environment["COTTON_SYNC_CLIPBOARD_TEXT"], Is.EqualTo("https://cloud.example/s/generated-token"));
                Assert.That(command, Does.Not.Contain("https://cloud.example/s/generated-token"));
            });
        }
    }
}
