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
        private static string CreateVfsLogonEvidenceBundle()
        {
            string evidenceDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "vfs-logon-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidenceDirectory);

            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "summary.txt"),
                new[]
                {
                    "CapturedAt: 2026-06-24T10:02:00.0000000Z",
                    "OS: captured: os.txt",
                    "Installed app: captured: installed-app.txt",
                    "Autostart registry: captured: registry-run.txt",
                    "Cotton processes: captured: processes.txt",
                    "Cotton process windows: captured: process-windows.txt",
                    "Cloud Files Explorer registrations: captured: registry-cloud-files-explorer.txt",
                    "Local root entries: captured: local-root-entries.csv",
                    "Installed profile self-test: exitCode=0; stdout=profile-self-test.stdout.log; stderr=profile-self-test.stderr.log",
                    "Diagnostics export: exitCode=0; stdout=diagnostics-export.stdout.log; stderr=diagnostics-export.stderr.log"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "os.txt"),
                new[]
                {
                    "Caption        : Microsoft Windows",
                    "LastBootUpTime : 2026-06-24T10:00:00.0000000Z"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "installed-app.txt"),
                new[]
                {
                    "Path: C:\\Program Files\\Cotton Sync\\Cotton.Sync.Desktop.exe",
                    "ProductVersion: 0.1.0",
                    "FileVersion: 0.1.0",
                    "Sha256: abc"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "registry-run.txt"),
                new[]
                {
                    "Key   : HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                    "Name  : Cotton Sync",
                    "Value : \"C:\\Program Files\\Cotton Sync\\Cotton.Sync.Desktop.exe\" --start-minimized"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "processes.txt"),
                new[]
                {
                    "ProcessId      : 1234",
                    "ExecutablePath : C:\\Program Files\\Cotton Sync\\Cotton.Sync.Desktop.exe",
                    "CommandLine    : \"C:\\Program Files\\Cotton Sync\\Cotton.Sync.Desktop.exe\" --start-minimized",
                    "CreationDate   : 2026-06-24T10:01:00.0000000Z"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "process-windows.txt"),
                new[]
                {
                    "ProcessId : 1234",
                    "IsForeground : False",
                    "VisibleWindowCount : 0"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-cloud-files-explorer.txt"),
                "MatchCount: 3");
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "local-root-entries.csv"),
                new[]
                {
                    "\"RelativePath\",\"FullPath\",\"Exists\",\"Attributes\",\"Length\",\"LastWriteTimeUtc\"",
                    "\".\",\"S:\\Cloud\",\"True\",\"Directory, ReparsePoint\",,"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "profile-self-test.stdout.log"),
                new[]
                {
                    "Cotton Sync Desktop self-test",
                    "[OK] Authentication state - Stored session available",
                    "[OK] Autostart adapter - Enabled",
                    "[OK] Windows virtual files - Windows Cloud Files API is available.",
                    "[OK] Local root: Cloud - S:\\Cloud",
                    "Result: passed"
                });
            File.WriteAllText(Path.Combine(evidenceDirectory, "diagnostics-export.stdout.log"), "Diagnostics exported");
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "run-vfs-logon-evidence-capture.log"),
                new[]
                {
                    "RunnerStartedAt: 2026-06-24T10:01:00.0000000Z",
                    "TaskRegisteredAt: 2026-06-24T09:59:00.0000000Z",
                    "LatestInteractiveLogonAt: 2026-06-24T10:00:00.0000000Z",
                    "TaskName: Cotton Sync VFS Logon Evidence Capture",
                    "RunnerUser: DESKTOP\\User",
                    "RunnerSessionId: 2",
                    "RunnerProcessId: 4242",
                    "RunnerInteractive: True",
                    "Cotton VFS release evidence captured: S:\\Evidence",
                    "CaptureExitCode: 0",
                    "RunnerFinishedAt: 2026-06-24T10:02:00.0000000Z",
                    "TaskUnregistered: True"
                });

            return evidenceDirectory;
        }

    }
}
