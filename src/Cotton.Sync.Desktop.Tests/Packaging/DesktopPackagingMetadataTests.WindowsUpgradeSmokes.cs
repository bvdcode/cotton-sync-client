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
        public void WindowsStartMenuLaunchSmokeScript_VerifiesShortcutTargetAndProcessLifecycle()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-start-menu-launch.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$ShortcutPath"));
                Assert.That(script, Does.Contain("[string]$ExpectedExecutablePath"));
                Assert.That(script, Does.Contain("WScript.Shell"));
                Assert.That(script, Does.Contain("CreateShortcut($resolvedShortcut)"));
                Assert.That(script, Does.Contain("Start Menu shortcut target was"));
                Assert.That(script, Does.Contain("Get-CimInstance Win32_Process"));
                Assert.That(script, Does.Contain("Start-Process -FilePath $resolvedShortcut"));
                Assert.That(script, Does.Contain("Start Menu shortcut did not launch"));
                Assert.That(script, Does.Contain("process exited immediately"));
                Assert.That(script, Does.Contain("Stop-Process -Id $process.ProcessId -Force"));
                Assert.That(script, Does.Contain("Verified Start Menu shortcut launch"));
            });
        }

        [Test]
        public void WindowsVersionMetadataVerifierScript_ChecksProductAndFileVersions()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-version-metadata.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$Executable"));
                Assert.That(script, Does.Contain("[string]$ExpectedProductVersion"));
                Assert.That(script, Does.Contain("[System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExecutable)"));
                Assert.That(script, Does.Contain("Remove-VersionMetadata"));
                Assert.That(script, Does.Contain("Get-SemVerCore"));
                Assert.That(script, Does.Contain("$versionInfo.FileMajorPart"));
                Assert.That(script, Does.Contain("$versionInfo.FileMinorPart"));
                Assert.That(script, Does.Contain("$versionInfo.FileBuildPart"));
                Assert.That(script, Does.Contain("ProductVersion was"));
                Assert.That(script, Does.Contain("FileVersion was"));
            });
        }

        [Test]
        public void WindowsGithubReleaseUpgradeSmokeScript_UsesPublishedReleaseInstaller()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-github-release-upgrade.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$ExpectedAppVersion"));
                Assert.That(script, Does.Contain("[string]$ReleaseTag = \"\""));
                Assert.That(script, Does.Contain("[string]$ExpectedCommit = $env:GITHUB_SHA"));
                Assert.That(script, Does.Contain("$ReleaseTag = \"v$ExpectedAppVersion\""));
                Assert.That(script, Does.Contain("gh release download $ReleaseTag"));
                Assert.That(script, Does.Contain("--pattern CottonSync-Windows.zip"));
                Assert.That(script, Does.Contain("--pattern CottonSync-Windows-Setup.exe"));
                Assert.That(script, Does.Contain("--pattern release-artifact-checksums.sha256"));
                Assert.That(script, Does.Contain("--pattern release-manifest.json"));
                Assert.That(script, Does.Contain("gh release view $ReleaseTag --repo $Repository --json body,assets"));
                Assert.That(script, Does.Contain("Assert-ReleaseMetadata -Manifest $manifest -ReleaseDetails $releaseDetails"));
                Assert.That(script, Does.Contain("Assert-ChecksumFile -Manifest $manifest -Path $releaseChecksums"));
                Assert.That(script, Does.Contain("Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name \"CottonSync-Windows.zip\" -Path $releaseZip"));
                Assert.That(script, Does.Contain("Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name \"CottonSync-Windows-Setup.exe\" -Path $releaseInstaller"));
                Assert.That(script, Does.Contain("Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name \"release-artifact-checksums.sha256\" -Path $releaseChecksums"));
                Assert.That(script, Does.Contain("## Cotton Sync Client $ExpectedAppVersion"));
                Assert.That(script, Does.Contain("## Changes"));
                Assert.That(script, Does.Contain("## Assets"));
                Assert.That(script, Does.Contain("$oldAppVersion = $ExpectedAppVersion + \"-ci-github-upgrade\""));
                Assert.That(script, Does.Contain("/DAppVersion=$oldAppVersion"));
                Assert.That(script, Does.Contain("-FilePath $releaseInstaller"));
                Assert.That(script, Does.Contain("[System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe)"));
                Assert.That(script, Does.Contain("$metadataStart = $actualVersion.IndexOf('+')"));
                Assert.That(script, Does.Contain("Upgraded desktop executable product version was"));
                Assert.That(script, Does.Contain("-ExpectedAppVersion $ExpectedAppVersion"));
                Assert.That(script, Does.Contain("Verified GitHub release Windows installer upgrade"));
            });
        }

        [Test]
        public void CiWorkflow_SmokesWindowsInstallerUpgrade()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Smoke desktop Windows installer upgrade"));
                Assert.That(workflow, Does.Contain("cotton-sync-old-installer"));
                Assert.That(workflow, Does.Contain("$ciVfsPlaceholderCount = 100000"));
                Assert.That(workflow, Does.Contain("/DAppVersion=0.0.1-ci-upgrade"));
                Assert.That(workflow, Does.Contain("/DOutputBaseFilename=cotton-sync-desktop-win-x64-0.0.1-ci-upgrade-setup"));
                Assert.That(workflow, Does.Contain("Old Windows installer was not created."));
                Assert.That(workflow, Does.Contain("-FilePath $oldInstaller"));
                Assert.That(workflow, Does.Contain("$currentInstallerPath = \".\\cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}-setup.exe\""));
                Assert.That(workflow, Does.Contain("$currentInstallStartInfo = [System.Diagnostics.ProcessStartInfo]::new()"));
                Assert.That(workflow, Does.Contain("$currentInstallStartInfo.FileName = (Resolve-Path $currentInstallerPath).Path"));
                Assert.That(workflow, Does.Contain("$currentInstallStartInfo.UseShellExecute = $false"));
                Assert.That(workflow, Does.Contain("[System.Diagnostics.Process]::Start($currentInstallStartInfo)"));
                Assert.That(workflow, Does.Contain("$currentInstall.WaitForExit()"));
                Assert.That(workflow, Does.Contain("$currentInstallExitCode = $currentInstall.ExitCode"));
                Assert.That(workflow, Does.Contain("\"/LaunchAfterUpdate=1\""));
                Assert.That(workflow, Does.Contain("\"/LaunchAfterUpdateDataDir=$dataDir\""));
                Assert.That(workflow, Does.Contain("Current Windows installer exited with code"));
                Assert.That(workflow, Does.Contain("Cotton Sync\\Cotton Sync.lnk"));
                Assert.That(workflow, Does.Contain("Cotton Sync\\Uninstall Cotton Sync.lnk"));
                Assert.That(workflow, Does.Contain("Upgraded Start Menu shortcut was not found."));
                Assert.That(workflow, Does.Contain("Upgraded Start Menu uninstall shortcut was not found."));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-shortcut-app-id.ps1"));
                Assert.That(workflow, Does.Contain("-ShortcutPath $startMenuShortcut"));
                Assert.That(workflow, Does.Contain("-ExpectedAppUserModelId \"Cotton.Sync.Desktop\""));
                Assert.That(workflow, Does.Contain("$upgradeSelfTestStdout = Join-Path $evidenceDir \"upgrade-self-test.stdout.log\""));
                Assert.That(workflow, Does.Contain("$upgradeSelfTestStderr = Join-Path $evidenceDir \"upgrade-self-test.stderr.log\""));
                Assert.That(workflow, Does.Contain("$upgradeSelfTest = Start-Process `"));
                Assert.That(workflow, Does.Contain("Upgraded desktop self-test exited with code"));
                Assert.That(workflow, Does.Contain("Write-Host \"Verified upgraded desktop self-test.\""));
                Assert.That(workflow, Does.Not.Contain("& $installedExe --self-test --data-dir"));
                Assert.That(workflow, Does.Contain("-PublishDirectory $installDir"));
                Assert.That(workflow, Does.Contain("-ExpectedIcon \"src/Cotton.Sync.Desktop/Assets/app.ico\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-version-metadata.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedProductVersion \"${{ steps.gitversion.outputs.SemVer }}\""));
                Assert.That(workflow, Does.Contain("-Label \"desktop publish executable\""));
                Assert.That(workflow, Does.Contain("-Label \"desktop zip executable\""));
                Assert.That(workflow, Does.Contain("-Label \"installed desktop executable\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-diagnostics-export.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedAppVersion \"${{ steps.gitversion.outputs.SemVer }}\""));
                Assert.That(workflow, Does.Contain("Windows uninstaller was not found after upgrade."));
                Assert.That(workflow, Does.Contain("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
                Assert.That(workflow, Does.Contain("Upgraded autostart registry value was not installed correctly."));
                Assert.That(workflow, Does.Contain("$expectedRunValue = \"`\"$installedExe`\" --start-minimized\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-autostart-launch.ps1"));
                Assert.That(workflow, Does.Contain("-AppExecutable $installedExe"));
                Assert.That(workflow, Does.Contain("$evidenceDir = Join-Path $env:RUNNER_TEMP \"cotton-sync-vfs-release-evidence\""));
                Assert.That(workflow, Does.Contain("$upgradeRelaunchReport = Join-Path $evidenceDir \"update-relaunch.txt\""));
                Assert.That(workflow, Does.Contain("-ReportPath $upgradeRelaunchReport"));
                Assert.That(workflow, Does.Contain("-AttachExistingProcess"));
                Assert.That(workflow, Does.Contain("$upgradeAutostartReport = Join-Path $env:RUNNER_TEMP \"cotton-sync-upgrade-autostart-launch.txt\""));
                Assert.That(workflow, Does.Contain("-ReportPath $upgradeAutostartReport"));
                Assert.That(workflow, Does.Not.Contain("Set-ItemProperty -Path $runKey -Name \"Cotton Sync\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-cloud-files-cleanup.ps1"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-vfs-release-evidence.ps1"));
                Assert.That(workflow, Does.Contain("-EvidenceDirectory $evidenceDir"));
                Assert.That(workflow, Does.Contain("-MinimumVfsPlaceholderCount $ciVfsPlaceholderCount"));
                Assert.That(workflow, Does.Contain("exit 0"));
                Assert.That(workflow, Does.Contain("Upgraded desktop executable remained after uninstall."));
                Assert.That(workflow, Does.Contain("Upgraded Start Menu shortcut remained after uninstall."));
                Assert.That(workflow, Does.Contain("Upgraded Start Menu uninstall shortcut remained after uninstall."));
                Assert.That(workflow, Does.Contain("Upgraded autostart registry value remained after uninstall."));
            });
        }

        [Test]
        public void CiWorkflow_SmokesPublishedGithubReleaseUpgrade()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Desktop GitHub Release Upgrade Smoke"));
                Assert.That(workflow, Does.Contain("- release"));
                Assert.That(workflow, Does.Contain("Smoke GitHub release Windows installer upgrade"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-github-release-upgrade.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedAppVersion \"${{ needs.linux.outputs.Version }}\""));
                Assert.That(workflow, Does.Contain("-ExpectedCommit \"${{ github.sha }}\""));
                Assert.That(workflow, Does.Contain("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}"));
            });
        }
    }
}
