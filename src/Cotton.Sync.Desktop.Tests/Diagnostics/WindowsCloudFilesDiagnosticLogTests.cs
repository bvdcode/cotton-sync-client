// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public class WindowsCloudFilesDiagnosticLogTests
    {
        private string _directory = string.Empty;
        private string _path = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cotton-cloud-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = DesktopAppPaths.CreateForDataDirectory(_directory).CloudFilesLogFilePath;
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void Record_PersistsDetailsImmediatelyWithoutExportingDiagnostics()
        {
            WindowsCloudFilesDiagnostics diagnostics = new();
            diagnostics.EnableFileLogging(_path);

            diagnostics.Record("convert", "failed", "pair-id", "local-root", "folder/video.mp4",
                "First line\r\nSecond line", unchecked((int)0x80070299));

            string[] lines = File.ReadAllLines(_path);
            Assert.Multiple(() =>
            {
                Assert.That(lines, Has.Length.EqualTo(1));
                Assert.That(lines[0], Does.StartWith(diagnostics.Snapshot()[0].TimestampUtc.ToString("O")));
                Assert.That(lines[0], Does.Contain("operation=convert; status=failed; pair=pair-id"));
                Assert.That(lines[0], Does.Contain("root=local-root; path=folder/video.mp4"));
                Assert.That(lines[0], Does.Contain("HRESULT=0x80070299"));
                Assert.That(lines[0], Does.Contain("First line\\r\\nSecond line"));
            });
        }

        [Test]
        public void EnableFileLogging_PreservesHistoryAfterBufferEvictionAndRestart()
        {
            WindowsCloudFilesDiagnostics first = new(capacity: 1);
            first.Record("before-startup", "completed");
            first.EnableFileLogging(_path);
            first.EnableFileLogging(_path);
            first.Record("before-restart", "failed");
            WindowsCloudFilesDiagnostics restarted = new();
            restarted.EnableFileLogging(_path);
            restarted.Record("after-restart", "completed");

            string[] lines = File.ReadAllLines(_path);
            Assert.Multiple(() =>
            {
                Assert.That(first.Snapshot(), Has.Count.EqualTo(1));
                Assert.That(lines, Has.Length.EqualTo(3));
                Assert.That(lines[0], Does.Contain("before-startup"));
                Assert.That(lines[1], Does.Contain("before-restart"));
                Assert.That(lines[2], Does.Contain("after-restart"));
            });
        }

        [Test]
        public void Record_RemovesSecretsBeforeWritingToDisk()
        {
            WindowsCloudFilesDiagnostics diagnostics = new();
            diagnostics.EnableFileLogging(_path);

            diagnostics.Record("download", "failed", details:
                "Authorization: Bearer test-access-token\n{\"password\":\"test-password\"}\nhttps://example.test/?access_token=query-token&item=1");

            string text = File.ReadAllText(_path);
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("[redacted]"));
                Assert.That(text, Does.Not.Contain("test-access-token"));
                Assert.That(text, Does.Not.Contain("test-password"));
                Assert.That(text, Does.Not.Contain("query-token"));
            });
        }

        [Test]
        public async Task Record_WritesCompleteEventsFromConcurrentCallbacks()
        {
            WindowsCloudFilesDiagnostics diagnostics = new();
            diagnostics.EnableFileLogging(_path);

            await Task.WhenAll(Enumerable.Range(0, 64).Select(index =>
                Task.Run(() => diagnostics.Record("hydrate", "completed", relativePath: $"folder/{index}.mp4"))));

            string[] lines = File.ReadAllLines(_path);
            Assert.That(lines, Has.Length.EqualTo(64));
            for (int index = 0; index < 64; index++)
            {
                Assert.That(lines.Count(line => line.Contains($"path=folder/{index}.mp4;", StringComparison.Ordinal)), Is.EqualTo(1));
            }
        }

        [Test]
        public void Record_RotatesAndRetainsThreePreviousLogs()
        {
            WindowsCloudFilesDiagnostics diagnostics = new();
            diagnostics.EnableFileLogging(_path);
            string details = new('x', (int)(DesktopLogRetention.MaxFileSizeBytes / 2));

            for (int index = 0; index < 5; index++)
            {
                diagnostics.Record($"event-{index}", "completed", details: details);
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_path), Does.Contain("event-4"));
                Assert.That(File.ReadAllText(_path + ".1"), Does.Contain("event-3"));
                Assert.That(File.ReadAllText(_path + ".3"), Does.Contain("event-1"));
                Assert.That(File.Exists(_path + ".4"), Is.False);
                Assert.That(new FileInfo(_path).Length, Is.LessThanOrEqualTo(DesktopLogRetention.MaxFileSizeBytes));
            });
        }

        [Test]
        public void Record_UnavailableLogDirectoryDoesNotInterruptCloudFileWork()
        {
            string blockedDirectory = Path.Combine(_directory, "blocked");
            File.WriteAllText(blockedDirectory, "existing file");
            WindowsCloudFilesDiagnostics diagnostics = new();
            diagnostics.EnableFileLogging(Path.Combine(blockedDirectory, "cloud-files.log"));

            Assert.DoesNotThrow(() => diagnostics.Record("convert", "failed", hResult: unchecked((int)0x80070299)));
            Assert.That(diagnostics.Snapshot(), Has.Count.EqualTo(1));
        }
    }
}
