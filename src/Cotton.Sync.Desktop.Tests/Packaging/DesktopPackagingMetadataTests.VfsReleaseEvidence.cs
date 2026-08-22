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
        public void WindowsVfsReleaseEvidenceScript_CapturesCleanWindowsState()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/capture-vfs-release-evidence.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$OutputDirectory = \"\""));
                Assert.That(script, Does.Contain("[string]$LocalRoot = (Join-Path $env:USERPROFILE \"Desktop\")"));
                Assert.That(script, Does.Contain("[string]$DataDirectory = (Join-Path $env:APPDATA \"Cotton\")"));
                Assert.That(script, Does.Contain("[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA \"Programs\\Cotton Sync\")"));
                Assert.That(script, Does.Contain("[string]$VfsSmokeDataDirectory = \"\""));
                Assert.That(script, Does.Contain("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
                Assert.That(script, Does.Contain("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\SyncRootManager"));
                Assert.That(script, Does.Contain("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace"));
                Assert.That(script, Does.Contain("HKCU\\Software\\Classes\\CLSID"));
                Assert.That(script, Does.Contain("HKCU\\Software\\Classes\\WOW6432Node\\CLSID"));
                Assert.That(script, Does.Contain("Cotton.Sync.Desktop.exe"));
                Assert.That(script, Does.Contain("-OperationTimeoutSec 2"));
                Assert.That(script, Does.Contain("Get-FileHash -LiteralPath $appExecutable -Algorithm SHA256"));
                Assert.That(script, Does.Contain("([datetime]$_.CreationDate).ToString(\"O\")"));
                Assert.That(script, Does.Contain("CottonReleaseEvidenceWindowProbe"));
                Assert.That(script, Does.Contain("GetVisibleWindowsForProcess"));
                Assert.That(script, Does.Contain("GetForegroundProcessId"));
                Assert.That(script, Does.Contain("Capture-ProcessWindows"));
                Assert.That(script, Does.Contain("$processes = @(Get-CottonProcess)"));
                Assert.That(script, Does.Contain("ProcessCount = 0"));
                Assert.That(script, Does.Contain("VisibleWindowCount = 0"));
                Assert.That(script, Does.Contain("Capture-CloudFilesExplorerRegistrations"));
                Assert.That(script, Does.Contain("registry-cloud-files-explorer.txt"));
                Assert.That(script, Does.Contain("Capture-RootEntries"));
                Assert.That(script, Does.Contain("Capture-LogTails"));
                Assert.That(script, Does.Contain("Capture-VfsSmokeLogs"));
                Assert.That(script, Does.Contain("vfs-smoke"));
                Assert.That(script, Does.Contain("Redact-Text"));
                Assert.That(script, Does.Contain("Add-PathRedaction -Path $LocalRoot -Placeholder \"<local root>\""));
                Assert.That(script, Does.Contain("Add-PathRedaction -Path $DataDirectory -Placeholder \"<data directory>\""));
                Assert.That(script, Does.Contain("Add-PathRedaction -Path $InstallDirectory -Placeholder \"<install directory>\""));
                Assert.That(script, Does.Contain("Add-PathRedaction -Path $VfsSmokeDataDirectory -Placeholder \"<vfs smoke data>\""));
                Assert.That(script, Does.Contain("Add-PathRedaction -Path $env:USERPROFILE -Placeholder \"<user profile>\""));
                Assert.That(script, Does.Contain("$redacted = $redacted.Replace($path, $placeholder)"));
                Assert.That(script, Does.Contain("CaptureScreenshot"));
                Assert.That(script, Does.Contain("RunSelfTest"));
                Assert.That(script, Does.Contain("RunProfileSelfTest"));
                Assert.That(script, Does.Contain("profile-self-test.stdout.log"));
                Assert.That(script, Does.Contain("RunDiagnosticsExport"));
                Assert.That(script, Does.Contain("Cotton VFS release evidence captured:"));
            });
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_ChecksRequiredEvidenceBundleFiles()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-vfs-release-evidence.ps1"));
            string alwaysKeepPopulationRunner = ReadDesktopPartialTypeSource(
                "Startup",
                "DesktopWindowsVirtualFilesSmokeRunner");
            const string inheritedAvailabilityProof =
                "Late-created descendants inherited Always keep before initial population completed.";

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$EvidenceDirectory"));
                Assert.That(script, Does.Contain("[int]$MinimumVfsPlaceholderCount = 500000"));
                Assert.That(script, Does.Contain("MinimumVfsPlaceholderCount must be greater than zero."));
                Assert.That(script, Does.Contain("summary.txt"));
                Assert.That(script, Does.Contain("installed-app.txt"));
                Assert.That(script, Does.Contain("registry-run.txt"));
                Assert.That(script, Does.Contain("autostart-launch.txt"));
                Assert.That(script, Does.Contain("update-relaunch.txt"));
                Assert.That(script, Does.Contain("post-uninstall-cleanup.txt"));
                Assert.That(script, Does.Contain("post-reinstall-cleanup.txt"));
                Assert.That(script, Does.Contain("post-upgrade-cleanup.txt"));
                Assert.That(script, Does.Contain("registry-cloud-files-explorer.txt"));
                Assert.That(script, Does.Contain("process-windows.txt"));
                Assert.That(script, Does.Contain("local-root-entries.csv"));
                Assert.That(script, Does.Contain("Assert-Contains -Content $localRootEntries -Expected '\".\"'"));
                Assert.That(script, Does.Contain("ConvertFrom-Csv"));
                Assert.That(script, Does.Contain("local-root-entries.csv did not prove the local root existed during evidence capture."));
                Assert.That(script, Does.Contain("local-root-entries.csv did not prove the local root was a directory during evidence capture."));
                Assert.That(script, Does.Contain("self-test.stdout.log"));
                Assert.That(script, Does.Contain("diagnostics-export.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-desktop-session-restore\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-shell-share-link-targets\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-replace-cloud-only-upload\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-excel-atomic-save\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-provider-metadata-user-edit\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-local-rename-after-provider-write\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-local-move-after-provider-write\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-explorer-always-keep\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-explorer-always-keep-during-population\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain(inheritedAvailabilityProof));
                Assert.That(alwaysKeepPopulationRunner, Does.Contain(inheritedAvailabilityProof));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-leave-registered\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-reconnect-existing\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-initial-streaming-logging\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-steady-state-repeat\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("Installed self-test: exitCode=0;"));
                Assert.That(script, Does.Contain("Diagnostics export: exitCode=0;"));
                Assert.That(script, Does.Contain("ObservedForeground: False"));
                Assert.That(script, Does.Contain("LaunchMode: attached-existing"));
                Assert.That(script, Does.Contain("CleanupRemaining: 0"));
                Assert.That(script, Does.Contain("visual-states.txt"));
                Assert.That(script, Does.Contain("Assert-VisualStateStableObservationMinimum -Content $visualStates -Scenario \"update-download-progress\" -MinimumSeconds 5"));
                Assert.That(script, Does.Contain("Assert-VisualStateStableObservationMinimum -Content $visualStates -Scenario \"update-install-progress\" -MinimumSeconds 5"));
                Assert.That(script, Does.Contain("Assert-VisualStateSamples -Content $visualStates -Scenario \"update-download-progress\" -MinimumSamples 5"));
                Assert.That(script, Does.Contain("Assert-VisualStateSamples -Content $visualStates -Scenario \"update-install-progress\" -MinimumSamples 5"));
                Assert.That(script, Does.Contain("Scenario: virtual-files-seeding;Status=Syncing;StableObservationSeconds=30;Samples="));
                Assert.That(script, Does.Contain("CheckedScope: HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\SyncRootManager"));
                Assert.That(script, Does.Contain("CheckedScope: HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace"));
                Assert.That(script, Does.Contain("CheckedScope: HKCU:\\Software\\Classes\\CLSID"));
                Assert.That(script, Does.Contain("CheckedScope: HKCU:\\Software\\Classes\\WOW6432Node\\CLSID"));
                Assert.That(script, Does.Contain("RemainingRegistrationCount: 0"));
                Assert.That(script, Does.Contain("No Cloud Files or Explorer registration was captured before uninstall."));
                Assert.That(script, Does.Contain("VFS smoke logs: captured:"));
                Assert.That(script, Does.Contain("Desktop startup restored the saved signed-in session."));
                Assert.That(script, Does.Contain("Desktop startup reconnected the persisted Cloud Files sync root."));
                Assert.That(script, Does.Contain("Desktop startup restore did not start a full sync or placeholder reseed pass."));
                Assert.That(script, Does.Contain("Uploaded replacement parent directory Cloud Files status was finalized."));
                Assert.That(script, Does.Contain("Explorer shell status settled for uploaded replacement parent directory."));
                Assert.That(script, Does.Contain("Cloud Files sync root left registered for process restart smoke."));
                Assert.That(script, Does.Contain("Existing remote-only placeholder is available before reconnect hydration."));
                Assert.That(script, Does.Contain("Initial VFS trace log contains large-run metrics."));
                Assert.That(script, Does.Contain("Metric excerpt:"));
                Assert.That(script, Does.Contain("placeholders/sec"));
                Assert.That(script, Does.Contain("dirs/sec"));
                Assert.That(script, Does.Contain("files/sec"));
                Assert.That(script, Does.Contain("state write rate="));
                Assert.That(script, Does.Contain("rows/sec"));
                Assert.That(script, Does.Contain("$MinimumVfsPlaceholderCount"));
                Assert.That(script, Does.Contain("$minimumVfsPlaceholderCountText"));
                Assert.That(script, Does.Contain("Initial VFS runtime health captured."));
                Assert.That(script, Does.Contain("workingSetBytes="));
                Assert.That(script, Does.Contain("privateMemoryBytes="));
                Assert.That(script, Does.Contain("threadCount="));
                Assert.That(script, Does.Contain("handleCount="));
                Assert.That(script, Does.Contain("Repeating Explorer Always keep on this device was idempotent."));
                Assert.That(script, Does.Contain("downloadsBeforeRepeat=1"));
                Assert.That(script, Does.Contain("downloadsAfterRepeat=1"));
                Assert.That(script, Does.Contain("Steady-state repeat pass used scoped path validation without local placeholder-tree scanning."));
                Assert.That(script, Does.Contain("fullLocalScans=0"));
                Assert.That(script, Does.Contain("metadataTreeScans=0"));
                Assert.That(script, Does.Contain("pathLookups=1"));
                Assert.That(script, Does.Contain("placeholderWrites=0"));
                Assert.That(script, Does.Contain("IsForeground"));
                Assert.That(script, Does.Contain("VisibleWindowCount"));
                Assert.That(script, Does.Contain("Cotton Sync became the foreground window during evidence capture."));
                Assert.That(script, Does.Contain("Cotton Sync had visible windows during evidence capture."));
                Assert.That(script, Does.Contain("failed:"));
                Assert.That(script, Does.Contain("Verified VFS release evidence bundle"));
            });
        }

        [Test]
        public void WindowsVfsLogonEvidenceVerifierScript_ChecksInstalledProfileEvidence()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-vfs-logon-evidence.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$EvidenceDirectory"));
                Assert.That(script, Does.Contain("LastBootUpTime"));
                Assert.That(script, Does.Contain("registry-run.txt"));
                Assert.That(script, Does.Contain("processes.txt"));
                Assert.That(script, Does.Contain("Read-FormatListRecords"));
                Assert.That(script, Does.Contain("registry-run.txt did not reference the installed executable path"));
                Assert.That(script, Does.Contain("processes.txt did not contain a running installed executable matching the captured HKCU Run command"));
                Assert.That(script, Does.Contain("process-windows.txt"));
                Assert.That(script, Does.Contain("registry-cloud-files-explorer.txt"));
                Assert.That(script, Does.Contain("local-root-entries.csv"));
                Assert.That(script, Does.Contain("profile-self-test.stdout.log"));
                Assert.That(script, Does.Contain("[OK] Authentication state - Stored session available"));
                Assert.That(script, Does.Contain("[OK] Autostart adapter - Enabled"));
                Assert.That(script, Does.Contain("[OK] Windows virtual files"));
                Assert.That(script, Does.Contain("[OK] Local root:"));
                Assert.That(script, Does.Contain("run-vfs-logon-evidence-capture.log"));
                Assert.That(script, Does.Contain("RunnerStartedAt:"));
                Assert.That(script, Does.Contain("TaskRegisteredAt:"));
                Assert.That(script, Does.Contain("LatestInteractiveLogonAt:"));
                Assert.That(script, Does.Contain("RunnerUser:"));
                Assert.That(script, Does.Contain("RunnerSessionId:"));
                Assert.That(script, Does.Contain("RunnerProcessId:"));
                Assert.That(script, Does.Contain("RunnerInteractive: True"));
                Assert.That(script, Does.Contain("Read-EvidenceTimestamp"));
                Assert.That(script, Does.Contain("VFS logon evidence was not captured after a newer interactive Windows logon."));
                Assert.That(script, Does.Contain("VFS logon evidence runner executed in Windows session 0"));
                Assert.That(script, Does.Contain("CaptureExitCode: 0"));
                Assert.That(script, Does.Contain("TaskUnregistered: True"));
                Assert.That(script, Does.Contain("No Cloud Files or Explorer registration was captured after logon."));
                Assert.That(script, Does.Contain("Verified VFS logon evidence bundle"));
            });
        }

        [Test]
        public void WindowsVfsLogonEvidenceCaptureRegistrationScript_RegistersOneShotProfileCapture()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/register-vfs-logon-evidence-capture.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("Register-ScheduledTask"));
                Assert.That(script, Does.Contain("Assert-RegisteredTaskMatchesRunner"));
                Assert.That(script, Does.Contain("VFS logon evidence capture task was not registered"));
                Assert.That(script, Does.Contain("VFS logon evidence capture task action does not reference the current runner"));
                Assert.That(script, Does.Contain("VFS logon evidence capture task working directory mismatch"));
                Assert.That(script, Does.Contain("Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue"));
                Assert.That(script, Does.Contain("New-ScheduledTaskTrigger -AtLogOn"));
                Assert.That(script, Does.Contain("New-ScheduledTaskPrincipal"));
                Assert.That(script, Does.Contain("$allowedTaskNamePrefix = \"Cotton Sync VFS Logon Evidence Capture\""));
                Assert.That(script, Does.Contain("Assert-SafeTaskName -Value $TaskName"));
                Assert.That(script, Does.Contain("TaskName must start with"));
                Assert.That(script, Does.Contain("ConvertTo-CommandLineArgument"));
                Assert.That(script, Does.Contain("Assert-RequiredValue -Name \"OutputDirectory\""));
                Assert.That(script, Does.Contain("[switch]$ValidateOnly"));
                Assert.That(script, Does.Contain("Resolve-RequiredDirectory -Name \"Local root\""));
                Assert.That(script, Does.Contain("Resolve-RequiredDirectory -Name \"Data directory\""));
                Assert.That(script, Does.Contain("Resolve-RequiredDirectory -Name \"Install directory\""));
                Assert.That(script, Does.Contain("Installed desktop executable was not found"));
                Assert.That(script, Does.Contain("Autostart registry value was not ready for logon capture."));
                Assert.That(script, Does.Contain("$expectedRunValue += \" --data-dir `\"$ProfileDataDirectory`\"\""));
                Assert.That(script, Does.Contain("Assert-InstalledAutostart -ExecutablePath $installedExecutable -ProfileDataDirectory $resolvedDataDirectory"));
                Assert.That(script, Does.Contain("Invoke-ProfileSelfTestPreflight"));
                Assert.That(script, Does.Contain("--self-test"));
                Assert.That(script, Does.Contain("[OK] Authentication state - Stored session available"));
                Assert.That(script, Does.Contain("[OK] Autostart adapter - Enabled"));
                Assert.That(script, Does.Contain("[OK] Windows virtual files"));
                Assert.That(script, Does.Contain("[OK] Local root:"));
                Assert.That(script, Does.Contain("Validated VFS logon evidence capture inputs."));
                Assert.That(script, Does.Contain("capture-vfs-release-evidence.ps1"));
                Assert.That(script, Does.Contain("$resolvedLocalRoot"));
                Assert.That(script, Does.Contain("$resolvedDataDirectory"));
                Assert.That(script, Does.Contain("$resolvedInstallDirectory"));
                Assert.That(script, Does.Contain("-RunProfileSelfTest"));
                Assert.That(script, Does.Contain("-RunDiagnosticsExport"));
                Assert.That(script, Does.Contain("RunnerStartedAt:"));
                Assert.That(script, Does.Contain("TaskRegisteredAt:"));
                Assert.That(script, Does.Contain("LatestInteractiveLogonAt:"));
                Assert.That(script, Does.Contain("Win32_LogonSession"));
                Assert.That(script, Does.Contain("-OperationTimeoutSec 2"));
                Assert.That(script, Does.Contain("RunnerUser:"));
                Assert.That(script, Does.Contain("RunnerSessionId:"));
                Assert.That(script, Does.Contain("RunnerProcessId:"));
                Assert.That(script, Does.Contain("RunnerInteractive:"));
                Assert.That(script, Does.Contain("CaptureExitCode:"));
                Assert.That(script, Does.Contain("RunnerFinishedAt:"));
                Assert.That(script, Does.Contain("TaskUnregistered:"));
                Assert.That(script, Does.Contain("Unregister-ScheduledTask -TaskName $taskNameLiteral"));
                Assert.That(script, Does.Contain("run-vfs-logon-evidence-capture.log"));
                Assert.That(script, Does.Contain("Removed VFS logon evidence capture task"));
            });
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_AcceptsCompleteEvidenceBundle()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.EqualTo(0), output);
                Assert.That(output, Does.Contain("Verified VFS release evidence bundle"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }
    }
}
