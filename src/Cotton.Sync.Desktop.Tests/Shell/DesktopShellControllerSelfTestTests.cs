// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerSelfTestTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            DesktopAuthDiagnosticsState.ResetForTests();
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-shell-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public async Task RunSelfTestAsync_IncludesReleaseRequiredChecks()
        {
            using DesktopShellController controller = CreateController();

            DesktopSelfTestSnapshot result = await controller.RunSelfTestAsync();

            string[] names = result.Items.Select(static item => item.Name).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(names, Does.Contain("Preferences database"));
                Assert.That(names, Does.Contain("Sync pair database"));
                Assert.That(names, Does.Contain("Sync state database"));
                Assert.That(names, Does.Contain("Authentication state"));
                Assert.That(names, Does.Contain("Token storage"));
                Assert.That(names, Does.Contain("Desktop icon"));
                Assert.That(names, Does.Contain("Update cache"));
                Assert.That(names, Does.Contain("Desktop platform"));
                Assert.That(names, Does.Contain("Tray lifecycle"));
                Assert.That(names, Does.Contain("Windows virtual files"));
                Assert.That(names, Does.Contain("Notification adapter"));
                Assert.That(names, Does.Contain("File watcher"));
                Assert.That(names, Does.Contain("Server identity"));
                Assert.That(names, Does.Contain("Desktop sync change feed"));
            });
        }


    }
}
