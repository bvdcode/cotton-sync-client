// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests
{
    public class RemoteTreeScanProgressReporterTests
    {
        [Test]
        public void Report_ClampsUnderreportedExpectedEntriesToCompletedCount()
        {
            RecordingProgress<SyncRunProgress> progress = new();
            SyncRunOptions options = new()
            {
                RunProgress = progress,
            };
            RemoteTreeScanProgressReporter reporter = new(options, DateTime.UtcNow);
            RemoteTreeScanProgress remoteProgress = new(
                filesScanned: 100,
                directoriesScanned: 2,
                currentPath: "Docs/latest.txt",
                entriesExpected: 101);

            Assert.DoesNotThrow(() => reporter.Report(remoteProgress));

            Assert.Multiple(() =>
            {
                Assert.That(progress.Values, Has.Count.EqualTo(1));
                Assert.That(progress.Values[0].FilesCompleted, Is.EqualTo(102));
                Assert.That(progress.Values[0].FilesTotal, Is.EqualTo(102));
                Assert.That(progress.Values[0].CurrentPath, Is.EqualTo("Docs/latest.txt"));
            });
        }

        private class RecordingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
            }
        }
    }
}
