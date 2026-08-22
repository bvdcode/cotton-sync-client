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
        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingDesktopSessionRestoreLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-desktop-session-restore"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-desktop-session-restore\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingDesktopSessionNoReseedProof()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
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
                        "Result: passed",
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                string normalizedOutput = NormalizePowerShellErrorOutput(output);
                Assert.That(
                    normalizedOutput,
                    Does.Contain("Desktop startup restore did not start a full sync or placeholder reseed pass."));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingReplaceCloudOnlyUploadLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-replace-cloud-only-upload"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-replace-cloud-only-upload\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingLocalRenameAfterProviderWriteLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-local-rename-after-provider-write"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-local-rename-after-provider-write\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingProviderMetadataUserEditLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-provider-metadata-user-edit"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-provider-metadata-user-edit\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingExcelAtomicSaveLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-excel-atomic-save"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-excel-atomic-save\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingLocalMoveAfterProviderWriteLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-local-move-after-provider-write"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-local-move-after-provider-write\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingLocalRootEntry()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllText(
                    Path.Combine(evidenceDirectory, "local-root-entries.csv"),
                    "\"RelativePath\",\"FullPath\",\"Exists\",\"Attributes\",\"Length\",\"LastWriteTimeUtc\"");

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("local-root-entries.csv did not contain expected text: \".\""));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingLocalRootPath()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllText(
                    Path.Combine(evidenceDirectory, "local-root-entries.csv"),
                    string.Join(
                        Environment.NewLine,
                        "\"RelativePath\",\"FullPath\",\"Exists\",\"Attributes\",\"Length\",\"LastWriteTimeUtc\"",
                        "\".\",\"S:\\Missing\",\"False\",\"\",\"\",\"\""));

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("local-root-entries.csv did not prove the local root existed during evidence capture."));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingCloudFilesRegistration()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllText(
                    Path.Combine(evidenceDirectory, "registry-cloud-files-explorer.txt"),
                    "MatchCount: 0");

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("No Cloud Files or Explorer registration was captured before uninstall."));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingSteadyStateRepeatLogs()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                Directory.Delete(
                    Path.Combine(evidenceDirectory, "vfs-smoke", "phase-steady-state-repeat"),
                    recursive: true);

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(
                    output,
                    Does.Contain("vfs-smoke\\phase-steady-state-repeat\\cloud-files-vfs-smoke.stdout.log"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsVisibleStartupWindowCapture()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "process-windows.txt"),
                    new[]
                    {
                        "IsForeground : False",
                        "VisibleWindowCount : 1"
                    });

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("Cotton Sync had visible windows during evidence capture."));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }
    }
}
