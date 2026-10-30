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
        public void WindowsStartMenuLaunchSmokeScript_VerifiesShortcutTargetAndProcessLifecycle()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-start-menu-launch.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$ShortcutPath"));
                Assert.That(script, Does.Contain("[string]$ExpectedExecutablePath"));
                Assert.That(script, Does.Contain("[string]$StartupLogPath"));
                Assert.That(script, Does.Contain("Desktop local storage initialized. ProcessId="));
                Assert.That(script, Does.Contain("$existingLogLines -notcontains $_"));
                Assert.That(script, Does.Contain("did not initialize local storage"));
                Assert.That(script, Does.Not.Contain("Start-Sleep -Seconds 2"));
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
