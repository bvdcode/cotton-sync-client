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
        public void CiWorkflow_GatesWindowsReleaseOnUpdateDiscoverySmokeBeforePublishing()
        {
            string workflow = GetDesktopWorkflow();
            string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
            int updateDiscoverySmokeIndex = normalizedWorkflow.IndexOf(
                "Packaging/windows/smoke-update-discovery.ps1",
                StringComparison.Ordinal);
            int uploadInstallerIndex = normalizedWorkflow.IndexOf(
                "Upload desktop Windows installer artifact",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(updateDiscoverySmokeIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(uploadInstallerIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(updateDiscoverySmokeIndex, Is.LessThan(uploadInstallerIndex));
                Assert.That(
                    normalizedWorkflow,
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - tests\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
            });
        }

        [Test]
        public void CiWorkflow_GatesWindowsReleaseOnUpdateInstallSmokeBeforePublishing()
        {
            string workflow = GetDesktopWorkflow();
            string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
            int updateInstallSmokeIndex = normalizedWorkflow.IndexOf(
                "Packaging/windows/smoke-update-install-handoff.ps1",
                StringComparison.Ordinal);
            int uploadInstallerIndex = normalizedWorkflow.IndexOf(
                "Upload desktop Windows installer artifact",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(updateInstallSmokeIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(uploadInstallerIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(updateInstallSmokeIndex, Is.LessThan(uploadInstallerIndex));
            });
        }

        [Test]
        public void CiWorkflow_GatesWindowsReleaseOnShellShareLinkCopySmokeBeforePublishing()
        {
            string workflow = GetDesktopWorkflow();
            string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
            int shellShareLinkCopySmokeIndex = normalizedWorkflow.IndexOf(
                "Packaging/windows/smoke-shell-share-link-copy.ps1",
                StringComparison.Ordinal);
            int uploadInstallerIndex = normalizedWorkflow.IndexOf(
                "Upload desktop Windows installer artifact",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(shellShareLinkCopySmokeIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(uploadInstallerIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(shellShareLinkCopySmokeIndex, Is.LessThan(uploadInstallerIndex));
            });
        }

        [Test]
        public void CiWorkflow_GatesWindowsReleaseOnNotificationIdentitySmokeBeforePublishing()
        {
            string workflow = GetDesktopWorkflow();
            string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
            int notificationIdentitySmokeIndex = normalizedWorkflow.IndexOf(
                "Packaging/windows/smoke-notification-identity.ps1",
                StringComparison.Ordinal);
            int uploadInstallerIndex = normalizedWorkflow.IndexOf(
                "Upload desktop Windows installer artifact",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(notificationIdentitySmokeIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(uploadInstallerIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(notificationIdentitySmokeIndex, Is.LessThan(uploadInstallerIndex));
                Assert.That(
                    normalizedWorkflow,
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - tests\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
            });
        }

        [Test]
        public void CiWorkflow_GatesWindowsReleaseOnCloudFilesSelfTestTruthfulnessBeforePublishing()
        {
            string workflow = GetDesktopWorkflow();
            string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
            int cloudFilesTruthfulnessSmokeIndex = normalizedWorkflow.IndexOf(
                "Packaging/windows/smoke-cloud-files-self-test-truthfulness.ps1",
                StringComparison.Ordinal);
            int uploadInstallerIndex = normalizedWorkflow.IndexOf(
                "Upload desktop Windows installer artifact",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(cloudFilesTruthfulnessSmokeIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(uploadInstallerIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(cloudFilesTruthfulnessSmokeIndex, Is.LessThan(uploadInstallerIndex));
                Assert.That(
                    normalizedWorkflow,
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - tests\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
            });
        }

        [Test]
        public void WindowsShellShareLinkVerbSmokeScript_VerifiesInstallAndUninstallRegistryState()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-shell-share-link-verb.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$ExpectedExecutablePath = \"\""));
                Assert.That(script, Does.Contain("[switch]$ExpectAbsent"));
                Assert.That(script, Does.Contain(@"Software\Classes\*\shell\CottonSyncCopyShareLink"));
                Assert.That(script, Does.Contain(@"Software\Classes\Directory\shell\CottonSyncCopyShareLink"));
                Assert.That(script, Does.Contain("Copy Cotton Cloud share link"));
                Assert.That(script, Does.Contain("--copy-shell-share-link"));
                Assert.That(script, Does.Contain("Shell.Application"));
                Assert.That(script, Does.Contain("[System.IO.Path]::GetDirectoryName($resolvedPath)"));
                Assert.That(script, Does.Contain("[System.IO.Path]::GetFileName($resolvedPath)"));
                Assert.That(script, Does.Not.Contain("Split-Path -LiteralPath"));
                Assert.That(script, Does.Contain("Assert-ShellVerbVisibility"));
                Assert.That(script, Does.Contain("Assert-InstalledShellVerbInvocation"));
                Assert.That(script, Does.Contain("ConvertTo-PowerShellSingleQuotedString"));
                Assert.That(script, Does.Contain("--shell-share-link-smoke\", \"--server-url\", $ServerUrl"));
                Assert.That(script, Does.Contain(@"$arguments = @(""--server-url"""));
                Assert.That(script, Does.Contain("Start-Process -FilePath {0} -ArgumentList $arguments"));
                Assert.That(script, Does.Not.Contain("'$process = Start-Process -FilePath ' +"));
                Assert.That(script, Does.Contain("shell-share-link-command.stdout.log"));
                Assert.That(script, Does.Contain("Installed shell share-link verb command did not reference the smoke wrapper and target placeholder."));
                Assert.That(script, Does.Contain("Installed shell share-link verb command wrapper exited with code"));
                Assert.That(script, Does.Contain("shell-share-link-root\\synced-file.txt"));
                Assert.That(script, Does.Contain("ProtectedData]::Protect"));
                Assert.That(script, Does.Contain("shell-share-link-smoke-access"));
                Assert.That(script, Does.Contain("ShareLinkCopied: true"));
                Assert.That(script, Does.Contain("download-link"));
                Assert.That(script, Does.Contain("Explorer shell did not expose"));
                Assert.That(script, Does.Contain("Verified installed shell share-link verbs, Explorer visibility, and shell invocation."));
                Assert.That(script, Does.Contain("Verified installed shell share-link verbs and Explorer visibility."));
                Assert.That(script, Does.Contain("Verified installed shell share-link verbs were removed."));
            });
        }

        [Test]
        public void WindowsShellShareLinkCopySmokeScript_VerifiesInstalledCopyFlow()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-shell-share-link-copy.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataDirectory"));
                Assert.That(script, Does.Contain("--shell-share-link-smoke"));
                Assert.That(script, Does.Contain("--data-dir"));
                Assert.That(script, Does.Contain("Result: passed"));
                Assert.That(script, Does.Contain("Verified installed shell share-link copy flow."));
            });
        }

        [Test]
        public void WindowsUpdateVisualStatesSmokeScript_VerifiesInstalledUpdatePanelStates()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-update-visual-states.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataRoot = \"\""));
                Assert.That(script, Does.Contain("[string]$ReportPath = \"\""));
                Assert.That(script, Does.Contain("UIAutomationClient"));
                Assert.That(script, Does.Contain("--visual-smoke"));
                Assert.That(script, Does.Contain("update-download-progress"));
                Assert.That(script, Does.Contain("update-install-progress"));
                Assert.That(script, Does.Contain("virtual-files-seeding"));
                Assert.That(script, Does.Contain("Downloading update"));
                Assert.That(script, Does.Contain("Installing update"));
                Assert.That(script, Does.Contain("Making cloud files available"));
                Assert.That(script, Does.Contain("Processing queued changes"));
                Assert.That(script, Does.Contain("[bool]$RequireSettingsActions"));
                Assert.That(script, Does.Contain("-RequireSettingsActions $false"));
                Assert.That(script, Does.Contain("[string[]]$ExpectedNames = @()"));
                Assert.That(script, Does.Contain("-ExpectedNames @(\"Preparing cloud files\")"));
                Assert.That(script, Does.Contain("[string]$ExpectedProgressBarName = \"\""));
                Assert.That(script, Does.Contain("-ExpectedProgressBarName \"Open-ended cloud file progress\""));
                Assert.That(script, Does.Contain("did not expose expected progress bar"));
                Assert.That(script, Does.Contain("Preparing cloud files 118054 of 500000"));
                Assert.That(script, Does.Contain("118054 of 500000"));
                Assert.That(script, Does.Contain("118054 cloud items ready"));
                Assert.That(script, Does.Contain("[int]$StableObservationSeconds = 0"));
                Assert.That(script, Does.Contain("Assert-VisualStateSnapshot"));
                Assert.That(script, Does.Contain("-StableObservationSeconds 5"));
                Assert.That(script, Does.Contain("-StableObservationSeconds 30"));
                Assert.That(script, Does.Contain("Observed visual state '$Scenario' sample(s):"));
                Assert.That(script, Does.Contain("Scenario: $Scenario;Status=$ExpectedStatus;StableObservationSeconds=$StableObservationSeconds;Samples=$sampleCount"));
                Assert.That(script, Does.Contain("MaxSnapshotMs=$maxSnapshotMs"));
                Assert.That(script, Does.Contain("MaxSampleGapMs=$maxSampleGapMs"));
                Assert.That(script, Does.Contain("Result: passed"));
                Assert.That(script, Does.Contain("Set-Content -LiteralPath $ReportPath"));
                Assert.That(script, Does.Contain("ControlType]::ProgressBar"));
                Assert.That(script, Does.Contain("[string[]]$UnexpectedNames"));
                Assert.That(script, Does.Contain("Assert-NameMissing -Names $names -UnexpectedName $unexpectedName"));
                Assert.That(script, Does.Contain("Verified installed update and VFS visual states."));
            });
        }

        [Test]
        public void WindowsUpdateDiscoverySmokeScript_VerifiesMockReleaseThroughInstalledExecutable()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-update-discovery.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataDirectory"));
                Assert.That(script, Does.Contain("[string]$ExpectedUpdateVersion = \"\""));
                Assert.That(script, Does.Contain("Get-NextPatchVersion"));
                Assert.That(script, Does.Contain("python"));
                Assert.That(script, Does.Contain("http.server"));
                Assert.That(script, Does.Contain("release-manifest.json"));
                Assert.That(script, Does.Contain("CottonSync-Windows-Setup.exe"));
                Assert.That(script, Does.Contain("--update-discovery-smoke"));
                Assert.That(script, Does.Contain("--update-manifest-url"));
                Assert.That(script, Does.Contain("--expected-update-version"));
                Assert.That(script, Does.Contain("diagnostics.json"));
                Assert.That(script, Does.Contain("lastCheckStatus"));
                Assert.That(script, Does.Contain("lastCheckSource"));
                Assert.That(script, Does.Contain("latestVersion"));
                Assert.That(script, Does.Contain("hasPendingUpdate"));
                Assert.That(script, Does.Contain("pendingInstallerSizeBytes"));
                Assert.That(script, Does.Contain("Desktop update download completed"));
                Assert.That(script, Does.Contain("Verified update discovery smoke"));
            });
        }

        [Test]
        public void WindowsUpdateInstallSmokeScript_VerifiesInstalledInstallerHandoff()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-update-install-handoff.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataDirectory"));
                Assert.That(script, Does.Contain("CottonSync-Windows-Setup.cmd"));
                Assert.That(script, Does.Contain("--update-install-smoke"));
                Assert.That(script, Does.Contain("--update-installer-path"));
                Assert.That(script, Does.Contain("diagnostics.json"));
                Assert.That(script, Does.Contain("lastInstallLaunchStatus"));
                Assert.That(script, Does.Contain("lastInstallProcessId"));
                Assert.That(script, Does.Contain("Update installer startup probe check was not reported."));
                Assert.That(script, Does.Contain("exitedDuringProbe=(?<exited>True|False)"));
                Assert.That(script, Does.Contain("lastInstallExitCode"));
                Assert.That(script, Does.Contain("expected null while the installer was still running."));
                Assert.That(script, Does.Contain("Verified installed update install handoff."));
            });
        }

        [Test]
        public void WindowsAutostartLaunchSmokeScript_VerifiesRunCommandStaysHiddenToTray()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-autostart-launch.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$RunValueName = \"Cotton Sync\""));
                Assert.That(script, Does.Contain("[string]$ReportPath = \"\""));
                Assert.That(script, Does.Contain("[string]$DataDirectory = \"\""));
                Assert.That(script, Does.Contain("[switch]$AttachExistingProcess"));
                Assert.That(script, Does.Contain("DataDirectory cannot be used when attaching to an existing installer-launched process."));
                Assert.That(script, Does.Contain("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
                Assert.That(script, Does.Contain("$expectedRunValue = \"`\"$resolvedExecutable`\" --start-minimized\""));
                Assert.That(script, Does.Contain("$expectedRunValue += \" --data-dir `\"$DataDirectory`\"\""));
                Assert.That(script, Does.Contain("Autostart registry value was not installed correctly."));
                Assert.That(script, Does.Contain("CottonAutostartWindowProbe"));
                Assert.That(script, Does.Contain("GetVisibleWindowsForProcess"));
                Assert.That(script, Does.Contain("GetForegroundProcessId"));
                Assert.That(script, Does.Contain("-OperationTimeoutSec 2"));
                Assert.That(script, Does.Not.Contain("$_.CommandLine -match"));
                Assert.That(script, Does.Contain("Waiting for existing hidden startup process"));
                Assert.That(script, Does.Contain("Start-Process `"));
                Assert.That(script, Does.Contain("$launchArguments.Add(\"--start-minimized\")"));
                Assert.That(script, Does.Contain("$launchArguments.Add(\"--data-dir\")"));
                Assert.That(script, Does.Contain("-ArgumentList $launchArguments"));
                Assert.That(script, Does.Contain("command line did not include --start-minimized"));
                Assert.That(script, Does.Contain("created a visible top-level window"));
                Assert.That(script, Does.Contain("became the foreground window"));
                Assert.That(script, Does.Contain("Write-AutostartReport"));
                Assert.That(script, Does.Contain("Result: passed"));
                Assert.That(script, Does.Contain("LaunchMode: $(if ($AttachExistingProcess)"));
                Assert.That(script, Does.Contain("IsolationDataDirectory: $DataDirectory"));
                Assert.That(script, Does.Contain("ObservedForeground: $observedForeground"));
                Assert.That(script, Does.Contain("VisibleWindowCount: $($observedVisibleWindows.Count)"));
                Assert.That(script, Does.Contain("CleanupRemaining: $($cleanupProcesses.Count)"));
                Assert.That(script, Does.Contain("Verified installed autostart launch stayed hidden to tray"));
            });
        }
    }
}
