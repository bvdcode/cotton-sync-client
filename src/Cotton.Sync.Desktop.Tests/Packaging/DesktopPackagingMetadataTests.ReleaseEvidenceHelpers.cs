// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Packaging
{
    public partial class DesktopPackagingMetadataTests
    {
        private static string? GetProperty(XElement propertyGroup, string name)
        {
            return propertyGroup.Element(name)?.Value;
        }

        private static string CreateVfsReleaseEvidenceBundle(int placeholderCount = 500000)
        {
            string placeholderCountText = placeholderCount.ToString(
                "N0",
                CultureInfo.InvariantCulture);
            string placeholderCountRaw = placeholderCount.ToString(CultureInfo.InvariantCulture);
            string finalItemCountText = (placeholderCount + 1).ToString("N0", CultureInfo.InvariantCulture);
            string evidenceDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "vfs-release-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidenceDirectory);
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-desktop-session-restore"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-shell-share-link-targets"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-replace-cloud-only-upload"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-excel-atomic-save"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-provider-metadata-user-edit"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-local-rename-after-provider-write"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-local-move-after-provider-write"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-leave-registered"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-reconnect-existing"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-initial-streaming-logging"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-steady-state-repeat"));

            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "summary.txt"),
                new[]
                {
                    "Installed app: captured: installed-app.txt",
                    "Autostart registry: captured: registry-run.txt",
                    "Cotton process windows: captured: process-windows.txt",
                    "Cloud Files Explorer registrations: captured: registry-cloud-files-explorer.txt",
                    "Local root entries: captured: local-root-entries.csv",
                    "Log tails: captured 1 log file(s)",
                    "VFS smoke logs: captured: vfs-smoke; files=12",
                    "Installed self-test: exitCode=0; stdout=self-test.stdout.log; stderr=self-test.stderr.log",
                    "Diagnostics export: exitCode=0; stdout=diagnostics-export.stdout.log; stderr=diagnostics-export.stderr.log"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "installed-app.txt"),
                new[]
                {
                    "ProductVersion: 0.1.0",
                    "FileVersion: 0.1.0",
                    "Sha256: abc"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-run.txt"),
                "Cotton Sync --start-minimized");
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "autostart-launch.txt"),
                new[]
                {
                    "Result: passed",
                    "ExpectedRunValue: Cotton.Sync.Desktop.exe --start-minimized",
                    "CommandLine: Cotton.Sync.Desktop.exe --start-minimized",
                    "ObservedForeground: False",
                    "VisibleWindowCount: 0",
                    "CleanupRemaining: 0"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "update-relaunch.txt"),
                new[]
                {
                    "Result: passed",
                    "LaunchMode: attached-existing",
                    "ExpectedRunValue: Cotton.Sync.Desktop.exe --start-minimized",
                    "CommandLine: Cotton.Sync.Desktop.exe --start-minimized",
                    "ObservedForeground: False",
                    "VisibleWindowCount: 0",
                    "CleanupRemaining: 0"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "visual-states.txt"),
                new[]
                {
                    "Result: passed",
                    "Scenario: update-download-progress;Status=Downloading update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                    "Scenario: update-install-progress;Status=Installing update;StableObservationSeconds=5;Samples=10;MaxSnapshotMs=100;MaxSampleGapMs=600",
                    "Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples=60;MaxSnapshotMs=100;MaxSampleGapMs=500"
                });
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-uninstall-cleanup.txt"));
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-reinstall-cleanup.txt"));
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-upgrade-cleanup.txt"));
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "process-windows.txt"),
                new[]
                {
                    "IsForeground : False",
                    "VisibleWindowCount : 0"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-cloud-files-explorer.txt"),
                "MatchCount: 1");
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "local-root-entries.csv"),
                string.Join(
                    Environment.NewLine,
                    "\"RelativePath\",\"FullPath\",\"Exists\",\"Attributes\",\"Length\",\"LastWriteTimeUtc\"",
                    "\".\",\"S:\\Cloud\",\"True\",\"Directory\",\"\",\"2026-06-24T10:00:00.0000000Z\""));
            File.WriteAllText(Path.Combine(evidenceDirectory, "self-test.stdout.log"), "Result: passed");
            File.WriteAllText(Path.Combine(evidenceDirectory, "diagnostics-export.stdout.log"), "Diagnostics exported");
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "vfs-smoke", "cloud-files-vfs-smoke.stdout.log"),
                "Result: passed");
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-desktop-session-restore",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "Desktop startup restored the saved signed-in session.",
                    "Desktop startup used the remembered server for session restore.",
                    "Desktop startup reconnected the persisted Cloud Files sync root.",
                    "Desktop startup restore did not start a full sync or placeholder reseed pass.",
                    "Result: passed",
                });
            File.WriteAllText(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-shell-share-link-targets",
                    "cloud-files-vfs-smoke.stdout.log"),
                "Result: passed");
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-replace-cloud-only-upload",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Uploaded replacement file Cloud Files status was finalized.",
                    "PASS: Uploaded replacement parent directory Cloud Files status was finalized.",
                    "PASS: Uploaded replacement sync root Cloud Files status was finalized.",
                    "PASS: Explorer shell status settled for uploaded replacement file.",
                    "PASS: Explorer shell status settled for uploaded replacement parent directory.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-excel-atomic-save",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Excel-style atomic saves stayed scoped to exactly the two workbook paths.",
                    "PASS: Excel lock and temporary artifacts were ignored and removed.",
                    "PASS: Two Excel-style saves emitted one debounced scoped request.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-provider-metadata-user-edit",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Provider metadata attribute echo was suppressed without starting sync.",
                    "PASS: Real watcher preserved a user content edit after provider metadata finalization.",
                    "PASS: Post-finalization content edit stayed scoped and emitted one request.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-local-rename-after-provider-write",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Real watcher preserved both paths for a user rename after provider write suppression.",
                    "PASS: Provider-suppressed user rename stayed scoped and emitted one request.",
                    "PASS: File-system rename completed without duplicating the local file.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-local-move-after-provider-write",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Real watcher preserved delete and create paths for a cross-directory move after provider metadata finalization.",
                    "PASS: Cross-directory move stayed scoped and emitted one request.",
                    "PASS: File-system cross-directory move left exactly the target file.",
                    "PASS: Real watcher preserved the deleted source and created target for a directory move after placeholder repair metadata finalization.",
                    "PASS: Directory move after placeholder repair stayed scoped and emitted one request.",
                    "PASS: File-system directory move preserved the nested file only at the target.",
                    "Result: passed"
                });
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-explorer-always-keep"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-explorer-always-keep-during-population"));
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-explorer-always-keep",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Explorer shell exposed and invoked Always keep on this device.",
                    "PASS: Explorer Always keep hydrated the placeholder and kept it pinned.",
                    "PASS: Reading the Always-keep file used local hydrated content.",
                    "PASS: Repeating Explorer Always keep on this device was idempotent. downloadsBeforeRepeat=1, downloadsAfterRepeat=1",
                    "PASS: Always-keep placeholder Cloud Files status was finalized.",
                    "PASS: Explorer shell status settled for always-keep placeholder.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-explorer-always-keep-during-population",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Explorer shell invoked Always keep on the parent folder during population.",
                    "PASS: Explorer Always keep watcher event queued while initial population was active.",
                    "PASS: Late-created descendants inherited Always keep before initial population completed.",
                    "PASS: All early and late files became pinned and hydrated.",
                    "PASS: Second Explorer Always keep invocation removed pin without deleting hydrated content.",
                    "PASS: Third Explorer Always keep invocation restored pin without redownloading.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-leave-registered",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Cloud Files sync root left registered for process restart smoke.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-reconnect-existing",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Existing remote-only placeholder is available before reconnect hydration.",
                    "PASS: Cloud Files sync root unregistered after smoke.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-initial-streaming-logging",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Initial VFS streaming run created a large placeholder baseline without per-placeholder activities.",
                    $"PASS: Initial VFS streaming progress stayed on placeholder creation and completed cleanly. samples=4, placeholderSamples=3, finalItems={finalItemCountText}/{finalItemCountText}, completed=True, localScanSamples=0, remoteScanSamples=0, activities=0",
                    "PASS: Initial VFS trace log contains large-run metrics.",
                    $"Metric excerpt: Completed initial streaming Windows virtual-files population for Cloud: 1 directories discovered at 25 dirs/sec, {placeholderCountRaw} files discovered at 2500 files/sec, remote pages read=500, remote page latency total=2000 ms, avg=4 ms, max=10 ms, last=3 ms, {placeholderCountRaw} file items completed, {placeholderCountRaw} placeholders created or refreshed at 2500 placeholders/sec; state writes {placeholderCountRaw} file rows, file write batches 977, directory rows 1, state write rate=2500 rows/sec; managed heap start=1000000 bytes, completed=1500000 bytes, peak=2000000 bytes, delta=500000 bytes; activities retained 0/0",
                    "PASS: Initial VFS runtime health captured. before=workingSetBytes=100000000;privateMemoryBytes=80000000;threadCount=12;handleCount=200, after=workingSetBytes=150000000;privateMemoryBytes=120000000;threadCount=14;handleCount=250",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-steady-state-repeat",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    $"PASS: Steady-state repeat pass used scoped path validation without local placeholder-tree scanning. files={placeholderCountText}, syncElapsedMs=3000, streamingCrawls=1, fullLocalScans=0, metadataTreeScans=0, pathLookups=1, transfers=0, placeholderWrites=0",
                    "Result: passed"
                });

            return evidenceDirectory;
        }

        private static void WriteCleanupEvidence(string path)
        {
            File.WriteAllLines(
                path,
                new[]
                {
                    "Result: passed",
                    "CheckedScope: HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\SyncRootManager",
                    "CheckedScope: HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace",
                    "CheckedScope: HKCU:\\Software\\Classes\\CLSID",
                    "CheckedScope: HKCU:\\Software\\Classes\\WOW6432Node\\CLSID",
                    "RemainingRegistrationCount: 0"
                });
        }
    }
}
