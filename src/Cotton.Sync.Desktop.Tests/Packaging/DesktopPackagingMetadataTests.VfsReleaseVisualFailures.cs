// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Packaging
{
    public partial class DesktopPackagingMetadataTests
    {
        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingUpdateRelaunchEvidence()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.Delete(Path.Combine(evidenceDirectory, "update-relaunch.txt"));

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("update-relaunch.txt"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsTooFewUpdateVisualStateSamples()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "visual-states.txt"),
                    new[]
                    {
                        "Result: passed",
                        "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=5;Samples=1;MaxSnapshotMs=100;MaxSampleGapMs=0",
                        "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples=60;MaxSnapshotMs=100;MaxSampleGapMs=500"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("visual-states.txt reported too few samples for update-download-progress"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsShortUpdateVisualStateWindow()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "visual-states.txt"),
                    new[]
                    {
                        "Result: passed",
                        "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=0;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples=60;MaxSnapshotMs=100;MaxSampleGapMs=500"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("visual-states.txt reported too short stable observation window for update-download-progress"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsTooFewVfsVisualStateSamples()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "visual-states.txt"),
                    new[]
                    {
                        "Result: passed",
                        "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples=1;MaxSnapshotMs=100;MaxSampleGapMs=500"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("visual-states.txt reported too few samples for virtual-files-seeding"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsSlowVfsVisualStateSamples()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "visual-states.txt"),
                    new[]
                    {
                        "Result: passed",
                        "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples=60;MaxSnapshotMs=100;MaxSampleGapMs=6000"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("visual-states.txt reported slow MaxSampleGapMs for virtual-files-seeding"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsShortVfsVisualStateWindow()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "visual-states.txt"),
                    new[]
                    {
                        "Result: passed",
                        "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                        "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=6;Samples=60;MaxSnapshotMs=100;MaxSampleGapMs=500"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("StableObservationSeconds=30"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsZeroInitialStreamingThroughput()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(
                        evidenceDirectory,
                        "vfs-smoke",
                        "phase-initial-streaming-logging",
                        "cloud-files-vfs-smoke.stdout.log"),
                    new[]
                    {
                        "PASS: Initial VFS streaming run created a large placeholder baseline without per-placeholder activities.",
                        "PASS: Initial VFS streaming progress stayed on placeholder creation and completed cleanly. samples=4, placeholderSamples=3, finalItems=100,001/100,001, completed=True, localScanSamples=0, remoteScanSamples=0, activities=0",
                        "PASS: Initial VFS trace log contains large-run metrics.",
                        "Metric excerpt: Completed initial streaming Windows virtual-files population for Cloud: 1 directories discovered at 25 dirs/sec, 500000 files discovered at 0 files/sec, remote pages read=500, remote page latency total=2000 ms, avg=4 ms, max=10 ms, last=3 ms, 500000 file items completed, 500000 placeholders created or refreshed at 0 placeholders/sec; state writes 500000 file rows, file write batches 977, directory rows 1, state write rate=0 rows/sec; managed heap start=1000000 bytes, completed=1500000 bytes, peak=2000000 bytes, delta=500000 bytes; activities retained 0/0",
                        "PASS: Initial VFS runtime health captured. before=workingSetBytes=100000000;privateMemoryBytes=80000000;threadCount=12;handleCount=200, after=workingSetBytes=150000000;privateMemoryBytes=120000000;threadCount=14;handleCount=250",
                        "Result: passed"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("reported too small files/sec"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsInitialStreamingBelowLargeAccountScale()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(
                        evidenceDirectory,
                        "vfs-smoke",
                        "phase-initial-streaming-logging",
                        "cloud-files-vfs-smoke.stdout.log"),
                    new[]
                    {
                        "PASS: Initial VFS streaming run created a large placeholder baseline without per-placeholder activities.",
                        "PASS: Initial VFS streaming progress stayed on placeholder creation and completed cleanly. samples=4, placeholderSamples=3, finalItems=100,001/100,001, completed=True, localScanSamples=0, remoteScanSamples=0, activities=0",
                        "PASS: Initial VFS trace log contains large-run metrics.",
                        "Metric excerpt: Completed initial streaming Windows virtual-files population for Cloud: 1 directories discovered at 25 dirs/sec, 100000 files discovered at 2500 files/sec, remote pages read=100, remote page latency total=400 ms, avg=4 ms, max=10 ms, last=3 ms, 100000 file items completed, 100000 placeholders created or refreshed at 2500 placeholders/sec; state writes 100000 file rows, file write batches 196, directory rows 1, state write rate=2500 rows/sec; managed heap start=1000000 bytes, completed=1500000 bytes, peak=2000000 bytes, delta=500000 bytes; activities retained 0/0",
                        "PASS: Initial VFS runtime health captured. before=workingSetBytes=100000000;privateMemoryBytes=80000000;threadCount=12;handleCount=200, after=workingSetBytes=150000000;privateMemoryBytes=120000000;threadCount=14;handleCount=250",
                        "Result: passed"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("reported too small files discovered"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_AcceptsBoundedReleaseGateScaleWhenRequested()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle(100000);
            try
            {
                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory, minimumVfsPlaceholderCount: 100000);

                Assert.That(exitCode, Is.EqualTo(0), output);
                Assert.That(output, Does.Contain("Verified VFS release evidence bundle"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingPostUninstallCleanupEvidence()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.Delete(Path.Combine(evidenceDirectory, "post-uninstall-cleanup.txt"));

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("post-uninstall-cleanup.txt"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsCleanupReportWithoutCheckedScopes()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "post-uninstall-cleanup.txt"),
                    new[]
                    {
                        "Result: passed",
                        "RemainingRegistrationCount: 0"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("did not contain expected text: CheckedScope:"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }
    }
}
