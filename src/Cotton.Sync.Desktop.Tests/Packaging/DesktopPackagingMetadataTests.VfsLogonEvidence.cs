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
        public void WindowsVfsLogonEvidenceVerifierScript_AcceptsCompleteLogonEvidenceBundle()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.EqualTo(0), output);
                Assert.That(output, Does.Contain("Verified VFS logon evidence bundle"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsAutostartFromDifferentInstall()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "registry-run.txt"),
                    new[]
                    {
                        "Key   : HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                        "Name  : Cotton Sync",
                        "Value : \"C:\\Program Files\\Old Cotton Sync\\Cotton.Sync.Desktop.exe\" --start-minimized"
                    });

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("registry-run.txt did not reference the installed executable path"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsRunningProcessFromDifferentInstall()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "processes.txt"),
                    new[]
                    {
                        "ProcessId      : 1234",
                        "ExecutablePath : C:\\Program Files\\Old Cotton Sync\\Cotton.Sync.Desktop.exe",
                        "CommandLine    : \"C:\\Program Files\\Old Cotton Sync\\Cotton.Sync.Desktop.exe\" --start-minimized",
                        "CreationDate   : 2026-06-24T10:01:00.0000000Z"
                    });

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("processes.txt did not contain a running installed executable matching the captured HKCU Run command"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsRunningProcessMissingRunCommandArguments()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "registry-run.txt"),
                    new[]
                    {
                        "Key   : HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                        "Name  : Cotton Sync",
                        "Value : \"C:\\Program Files\\Cotton Sync\\Cotton.Sync.Desktop.exe\" --start-minimized --data-dir \"S:\\CottonSyncVfsQa\\profile\""
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

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("processes.txt did not contain a running installed executable matching the captured HKCU Run command"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsSignedOutProfileSelfTest()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "profile-self-test.stdout.log"),
                    new[]
                    {
                        "Cotton Sync Desktop self-test",
                        "[OK] Authentication state - Signed out",
                        "[OK] Autostart adapter - Enabled",
                        "[OK] Windows virtual files - Windows Cloud Files API is available.",
                        "[OK] Local root: Cloud - S:\\Cloud",
                        "Result: passed"
                    });

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("[OK] Authentication state - Stored session available"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsMissingRunnerLog()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                File.Delete(Path.Combine(evidenceDirectory, "run-vfs-logon-evidence-capture.log"));

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("run-vfs-logon-evidence-capture.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsSessionZeroRunnerLog()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                string runnerLogPath = Path.Combine(evidenceDirectory, "run-vfs-logon-evidence-capture.log");
                string runnerLog = File.ReadAllText(runnerLogPath)
                    .Replace("RunnerSessionId: 2", "RunnerSessionId: 0", StringComparison.Ordinal);
                File.WriteAllText(runnerLogPath, runnerLog);

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("VFS logon evidence runner executed in Windows session 0"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsEvidenceWithoutNewerInteractiveLogon()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                string runnerLogPath = Path.Combine(evidenceDirectory, "run-vfs-logon-evidence-capture.log");
                string runnerLog = File.ReadAllText(runnerLogPath)
                    .Replace(
                        "LatestInteractiveLogonAt: 2026-06-24T10:00:00.0000000Z",
                        "LatestInteractiveLogonAt: 2026-06-24T09:58:00.0000000Z",
                        StringComparison.Ordinal);
                File.WriteAllText(runnerLogPath, runnerLog);

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("VFS logon evidence was not captured after a newer interactive Windows logon."));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsProcessCreatedBeforeLatestLogon()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                string processesPath = Path.Combine(evidenceDirectory, "processes.txt");
                string processes = File.ReadAllText(processesPath)
                    .Replace(
                        "CreationDate   : 2026-06-24T10:01:00.0000000Z",
                        "CreationDate   : 2026-06-24T09:57:00.0000000Z",
                        StringComparison.Ordinal);
                File.WriteAllText(processesPath, processes);

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("created before the latest interactive Windows logon"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsProcessCreatedAfterRunnerStart()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                string processesPath = Path.Combine(evidenceDirectory, "processes.txt");
                string processes = File.ReadAllText(processesPath)
                    .Replace(
                        "CreationDate   : 2026-06-24T10:01:00.0000000Z",
                        "CreationDate   : 2026-06-24T10:01:30.0000000Z",
                        StringComparison.Ordinal);
                File.WriteAllText(processesPath, processes);

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("created after the post-logon capture runner started"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_RejectsMissingTaskUnregistrationProof()
        {
            string evidenceDirectory = CreateVfsLogonEvidenceBundle();
            try
            {
                string runnerLogPath = Path.Combine(evidenceDirectory, "run-vfs-logon-evidence-capture.log");
                string runnerLog = File.ReadAllText(runnerLogPath)
                    .Replace("TaskUnregistered: True" + Environment.NewLine, string.Empty, StringComparison.Ordinal);
                File.WriteAllText(runnerLogPath, runnerLog);

                (int exitCode, string output) = RunVfsLogonEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("TaskUnregistered: True"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }
    }
}
