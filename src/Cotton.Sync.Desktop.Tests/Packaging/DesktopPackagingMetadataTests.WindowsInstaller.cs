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
        public void WindowsInstallerScript_DefinesReleaseInstallLayout()
        {
            string installerScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/cotton-sync.iss"));

            Assert.Multiple(() =>
            {
                Assert.That(installerScript, Does.Contain("AppName=Cotton Sync"));
                Assert.That(installerScript, Does.Contain("AppVerName=Cotton Sync"));
                Assert.That(installerScript, Does.Contain("DefaultDirName={localappdata}\\Programs\\Cotton Sync"));
                Assert.That(installerScript, Does.Contain("DefaultGroupName=Cotton Sync"));
                Assert.That(installerScript, Does.Contain("PrivilegesRequired=lowest"));
                Assert.That(installerScript, Does.Contain("ArchitecturesAllowed=x64compatible"));
                Assert.That(installerScript, Does.Contain("#define OutputBaseFilename \"cotton-sync-desktop-win-x64-setup\""));
                Assert.That(installerScript, Does.Contain("OutputBaseFilename={#OutputBaseFilename}"));
                Assert.That(installerScript, Does.Contain("SetupIconFile={#IconFile}"));
                Assert.That(installerScript, Does.Contain("UninstallDisplayName=Cotton Sync"));
                Assert.That(installerScript, Does.Contain("UninstallDisplayIcon={app}\\Cotton.Sync.Desktop.exe"));
                Assert.That(installerScript, Does.Contain("#define AppMutexName \"CottonSyncDesktop_B671C18E_1E77_437C_AB9B_5C5C9D877E18\""));
                Assert.That(installerScript, Does.Contain("#define AppUserModelId \"" + DesktopAppIdentity.AppUserModelId + "\""));
                Assert.That(installerScript, Does.Not.Contain("AppMutex={#AppMutexName}"));
                Assert.That(installerScript, Does.Contain("CloseApplications=force"));
                Assert.That(installerScript, Does.Contain("RestartApplications=no"));
                Assert.That(installerScript, Does.Contain("InitializeUninstall"));
                Assert.That(installerScript, Does.Contain("StopInstalledAppForSilentUninstall"));
                Assert.That(installerScript, Does.Contain("Get-CimInstance Win32_Process"));
                Assert.That(installerScript, Does.Contain("Stop-Process -Id $_.ProcessId -Force"));
                Assert.That(installerScript, Does.Contain("Wait-Process -Id $_.ProcessId -Timeout 5"));
                Assert.That(installerScript, Does.Contain("CheckForMutexes('{#AppMutexName}')"));
                Assert.That(installerScript, Does.Contain("Sleep(250)"));
                Assert.That(installerScript, Does.Contain("Silent uninstall app mutex released"));
                Assert.That(installerScript, Does.Contain("Source: \"{#SourceDir}\\*\""));
                Assert.That(installerScript, Does.Contain("recursesubdirs createallsubdirs"));
                Assert.That(installerScript, Does.Contain("Cotton.Sync.Desktop.exe"));
                Assert.That(installerScript, Does.Contain("Name: \"{group}\\Cotton Sync\""));
                Assert.That(installerScript, Does.Contain("Name: \"{group}\\Uninstall Cotton Sync\""));
                Assert.That(installerScript, Does.Contain("Filename: \"{uninstallexe}\""));
                Assert.That(installerScript, Does.Contain("IconFilename: \"{app}\\Cotton.Sync.Desktop.exe\""));
                Assert.That(Regex.Matches(installerScript, "AppUserModelID: \"\\{#AppUserModelId\\}\"").Count, Is.EqualTo(2));
                Assert.That(installerScript, Does.Contain("Create a desktop shortcut"));
                Assert.That(installerScript, Does.Contain("Root: HKCU; Subkey: \"Software\\Microsoft\\Windows\\CurrentVersion\\Run\""));
                Assert.That(installerScript, Does.Contain("ValueName: \"Cotton Sync\""));
                Assert.That(installerScript, Does.Contain("ValueData: \"{code:GetAutostartLaunchCommand}\""));
                Assert.That(installerScript, Does.Contain("Flags: uninsdeletevalue"));
                Assert.That(installerScript, Does.Contain(@"Software\Classes\*\shell\CottonSyncCopyShareLink"));
                Assert.That(installerScript, Does.Contain(@"Software\Classes\Directory\shell\CottonSyncCopyShareLink"));
                Assert.That(installerScript, Does.Contain("Copy Cotton Cloud share link"));
                Assert.That(installerScript, Does.Contain("--copy-shell-share-link"));
                Assert.That(installerScript, Does.Contain("Flags: nowait postinstall; Check: ShouldOfferLaunchAfterInstall"));
                Assert.That(installerScript, Does.Contain("Parameters: \"{code:GetHiddenUpdateLaunchParameters}\"; Flags: nowait; Check: ShouldLaunchHiddenAfterUpdate"));
                Assert.That(installerScript, Does.Contain("function ShouldOfferLaunchAfterInstall(): Boolean;"));
                Assert.That(installerScript, Does.Contain("function ShouldLaunchHiddenAfterUpdate(): Boolean;"));
                Assert.That(installerScript, Does.Contain("function CommandLineQuoted(Value: String): String;"));
                Assert.That(installerScript, Does.Contain("function GetHiddenUpdateLaunchParameters(Value: String): String;"));
                Assert.That(installerScript, Does.Contain("function GetAutostartLaunchCommand(Value: String): String;"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdate|0}') <> '1'"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdate|0}') = '1'"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdateDataDir|}')"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:AutostartDataDir|}')"));
                Assert.That(installerScript, Does.Contain("CommandLineQuoted(ExpandConstant('{app}\\Cotton.Sync.Desktop.exe')) + ' --start-minimized'"));
                Assert.That(installerScript, Does.Contain("Result := '--start-minimized';"));
                Assert.That(installerScript, Does.Contain("Result := Result + ' --data-dir ' + CommandLineQuoted(DataDirectory);"));
                Assert.That(installerScript, Does.Not.Contain("AddQuotes(ExpandConstant('{app}\\Cotton.Sync.Desktop.exe'))"));
                Assert.That(installerScript, Does.Not.Contain("AddQuotes(DataDirectory)"));
                Assert.That(installerScript, Does.Contain("CurUninstallStepChanged"));
                Assert.That(installerScript, Does.Contain("RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'Cotton Sync')"));
            });
        }

        [Test]
        public void CiWorkflow_BuildsAndUploadsWindowsInstallerArtifact()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Install Inno Setup"));
                Assert.That(workflow, Does.Contain("choco install innosetup"));
                Assert.That(workflow, Does.Contain("INNO_SETUP_COMPILER"));
                Assert.That(workflow, Does.Contain("Package desktop Windows installer"));
                Assert.That(workflow, Does.Contain("Packaging/windows/cotton-sync.iss"));
                Assert.That(workflow, Does.Contain("/DIconFile=$iconFile"));
                Assert.That(workflow, Does.Contain("/DAppVersion=${{ steps.gitversion.outputs.SemVer }}"));
                Assert.That(workflow, Does.Contain("/DOutputBaseFilename=cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}-setup"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}-setup.exe"));
                Assert.That(workflow, Does.Contain("Upload desktop Windows installer artifact"));
                Assert.That(workflow, Does.Contain("name: desktop-windows-installer"));
            });
        }

        [Test]
        public void CiWorkflow_SmokesWindowsInstallerInstallAndUninstall()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Smoke desktop Windows installer"));
                Assert.That(workflow, Does.Contain("cotton-sync-installed"));
                Assert.That(workflow, Does.Contain("cotton-sync-installer-data"));
                Assert.That(workflow, Does.Contain("cotton-sync-vfs-release-evidence"));
                Assert.That(workflow, Does.Contain("$installLog = Join-Path $evidenceDir \"cotton-sync-install.log\""));
                Assert.That(workflow, Does.Contain("$uninstallLog = Join-Path $evidenceDir \"cotton-sync-uninstall.log\""));
                Assert.That(workflow, Does.Contain("$reinstallLog = Join-Path $evidenceDir \"cotton-sync-reinstall.log\""));
                Assert.That(workflow, Does.Contain("$reinstallUninstallLog = Join-Path $evidenceDir \"cotton-sync-reinstall-uninstall.log\""));
                Assert.That(workflow, Does.Contain("$contextReport = Join-Path $evidenceDir \"installer-smoke-context.txt\""));
                Assert.That(workflow, Does.Contain("$vfsSmokeDataDir = Join-Path $env:RUNNER_TEMP \"cotton-sync-vfs-self-test-truthfulness-data\""));
                Assert.That(workflow, Does.Contain("$vfsLocalRoot = Join-Path $env:RUNNER_TEMP \"cotton-sync-vfs-root\""));
                Assert.That(workflow, Does.Contain("$ciVfsPlaceholderCount = 100000"));
                Assert.That(workflow, Does.Contain("VfsPlaceholderCount: $ciVfsPlaceholderCount"));
                Assert.That(workflow, Does.Contain("$autostartReport = Join-Path $evidenceDir \"autostart-launch.txt\""));
                Assert.That(workflow, Does.Contain("$reinstallSelfTestStdout = Join-Path $evidenceDir \"reinstall-self-test.stdout.log\""));
                Assert.That(workflow, Does.Contain("$reinstallSelfTestStderr = Join-Path $evidenceDir \"reinstall-self-test.stderr.log\""));
                Assert.That(workflow, Does.Contain("New-Item -ItemType Directory -Path $evidenceDir -Force"));
                Assert.That(workflow, Does.Contain("New-Item -ItemType Directory -Path $vfsLocalRoot -Force"));
                Assert.That(workflow, Does.Contain("Set-Content -LiteralPath $contextReport -Encoding utf8"));
                Assert.That(workflow, Does.Contain("/VERYSILENT"));
                Assert.That(workflow, Does.Contain("/SUPPRESSMSGBOXES"));
                Assert.That(workflow, Does.Contain("/NORESTART"));
                Assert.That(workflow, Does.Contain("/TASKS="));
                Assert.That(workflow, Does.Contain("/DIR=$installDir"));
                Assert.That(workflow, Does.Contain("[Environment]::GetFolderPath(\"Programs\")"));
                Assert.That(workflow, Does.Contain("Cotton Sync\\Cotton Sync.lnk"));
                Assert.That(workflow, Does.Contain("Cotton Sync\\Uninstall Cotton Sync.lnk"));
                Assert.That(workflow, Does.Contain("Installed Start Menu shortcut was not found."));
                Assert.That(workflow, Does.Contain("Installed Start Menu uninstall shortcut was not found."));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-shortcut-app-id.ps1"));
                Assert.That(workflow, Does.Contain("-ShortcutPath $startMenuShortcut"));
                Assert.That(workflow, Does.Contain("-ExpectedAppUserModelId \"Cotton.Sync.Desktop\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-notification-identity.ps1"));
                Assert.That(workflow, Does.Contain("-DataDirectory (Join-Path $env:RUNNER_TEMP \"cotton-sync-notification-identity-data\")"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-start-menu-launch.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedExecutablePath $installedExe"));
                Assert.That(workflow, Does.Contain("Cotton.Sync.Desktop.exe\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-cloud-files-self-test-truthfulness.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-vfs-self-test-truthfulness-data"));
                Assert.That(workflow, Does.Contain("-LocalRoot $vfsLocalRoot"));
                Assert.That(
                    workflow,
                    Does.Contain("-AdditionalVfsSmokePhases @(\"desktop-session-restore\", \"shell-share-link-targets\", \"initial-streaming-logging\", \"steady-state-repeat\", \"replace-cloud-only-upload\", \"excel-atomic-save\", \"provider-metadata-user-edit\", \"local-rename-after-provider-write\", \"local-move-after-provider-write\", \"explorer-always-keep\", \"explorer-always-keep-missing-placeholder\", \"explorer-always-keep-during-population\")"));
                Assert.That(workflow, Does.Contain("timeout-minutes: 20"));
                Assert.That(workflow, Does.Contain("-InitialStreamingPlaceholderCount $ciVfsPlaceholderCount"));
                Assert.That(workflow, Does.Contain("-SteadyStateRepeatPlaceholderCount $ciVfsPlaceholderCount"));
                Assert.That(workflow, Does.Contain("function Invoke-InstalledVfsSmokePhase"));
                Assert.That(workflow, Does.Contain("\"--vfs-smoke-placeholder-count\""));
                Assert.That(workflow, Does.Contain("Write-Host \"Starting installed VFS smoke phase 'leave-registered'.\""));
                Assert.That(workflow, Does.Contain("Write-Host \"Starting installed VFS smoke phase 'reconnect-existing'.\""));
                Assert.That(workflow, Does.Contain("Write-Host \"Completed installed VFS smoke phase '$PhaseName'.\""));
                Assert.That(workflow, Does.Contain("Invoke-InstalledVfsSmokePhase -PhaseName \"leave-registered\""));
                Assert.That(workflow, Does.Contain("Invoke-InstalledVfsSmokePhase -PhaseName \"reconnect-existing\""));
                Assert.That(workflow, Does.Contain("phase-reconnect-existing"));
                Assert.That(workflow, Does.Contain("$reinstallSelfTest = Start-Process `"));
                Assert.That(workflow, Does.Contain("-ArgumentList @(\"--self-test\", \"--data-dir\", $dataDir)"));
                Assert.That(workflow, Does.Contain("Reinstalled desktop self-test exited with code"));
                Assert.That(workflow, Does.Contain("Write-Host \"Verified reinstalled desktop self-test.\""));
                Assert.That(workflow, Does.Contain("exit 0"));
                Assert.That(workflow, Does.Contain("-PublishDirectory $installDir"));
                Assert.That(workflow, Does.Contain("-AppExecutable $installedExe"));
                Assert.That(workflow, Does.Contain("-ExpectedIcon \"src/Cotton.Sync.Desktop/Assets/app.ico\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-diagnostics-export.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedAppVersion \"${{ steps.gitversion.outputs.SemVer }}\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-update-discovery.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-update-discovery-data"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-update-visual-states.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-update-visual-states-data"));
                Assert.That(workflow, Does.Contain("$visualStatesReport = Join-Path $evidenceDir \"visual-states.txt\""));
                Assert.That(workflow, Does.Contain("-ReportPath $visualStatesReport"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-update-install-handoff.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-update-install-data"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-shell-share-link-copy.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-shell-share-link-data"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-shell-share-link-verb.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedExecutablePath $installedExe"));
                Assert.That(workflow, Does.Contain("-InvocationDataDirectory (Join-Path $env:RUNNER_TEMP \"cotton-sync-shell-share-link-data\")"));
                Assert.That(workflow, Does.Contain("-InvokeInstalledVerb"));
                Assert.That(workflow, Does.Contain("Packaging/windows/capture-vfs-release-evidence.ps1"));
                Assert.That(workflow, Does.Contain("-OutputDirectory $evidenceDir"));
                Assert.That(workflow, Does.Not.Contain("\"S:\\CottonSyncVfsQa\\root\""));
                Assert.That(workflow, Does.Contain("-InstallDirectory $installDir"));
                Assert.That(workflow, Does.Contain("-VfsSmokeDataDirectory $vfsSmokeDataDir"));
                Assert.That(workflow, Does.Contain("-RunSelfTest"));
                Assert.That(workflow, Does.Contain("-RunDiagnosticsExport"));
                Assert.That(workflow, Does.Contain("-ExpectAbsent"));
                Assert.That(workflow, Does.Contain("unins000.exe"));
                Assert.That(workflow, Does.Contain("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
                Assert.That(workflow, Does.Contain("Autostart registry value was not installed correctly."));
                Assert.That(workflow, Does.Contain("\"/AutostartDataDir=$dataDir\""));
                Assert.That(workflow, Does.Contain("$expectedRunValue = \"`\"$installedExe`\" --start-minimized --data-dir `\"$dataDir`\"\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-autostart-launch.ps1"));
                Assert.That(workflow, Does.Contain("-AppExecutable $installedExe"));
                Assert.That(workflow, Does.Contain("-DataDirectory $dataDir"));
                Assert.That(workflow, Does.Contain("-ReportPath $autostartReport"));
                Assert.That(workflow, Does.Not.Contain("Set-ItemProperty -Path $runKey -Name \"Cotton Sync\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-cloud-files-cleanup.ps1"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-vfs-release-evidence.ps1"));
                Assert.That(workflow, Does.Contain("-EvidenceDirectory $evidenceDir"));
                Assert.That(workflow, Does.Contain("-MinimumVfsPlaceholderCount $ciVfsPlaceholderCount"));
                Assert.That(
                    Regex.Matches(workflow, "Packaging/windows/verify-cloud-files-cleanup.ps1").Count,
                    Is.GreaterThanOrEqualTo(3));
                Assert.That(workflow, Does.Contain("Installed desktop executable remained after uninstall."));
                Assert.That(workflow, Does.Contain("Install directory was not empty after uninstall."));
                Assert.That(workflow, Does.Contain("Windows reinstall exited with code"));
                Assert.That(workflow, Does.Contain("Reinstalled desktop executable was not found."));
                Assert.That(workflow, Does.Contain("Windows reinstall cleanup exited with code"));
                Assert.That(workflow, Does.Contain("Install directory was not empty after reinstall cleanup."));
                Assert.That(workflow, Does.Contain("$installLog = Join-Path $evidenceDir \"cotton-sync-upgrade-install.log\""));
                Assert.That(workflow, Does.Contain("$upgradeLog = Join-Path $evidenceDir \"cotton-sync-upgrade-current.log\""));
                Assert.That(workflow, Does.Contain("$uninstallLog = Join-Path $evidenceDir \"cotton-sync-upgrade-uninstall.log\""));
                Assert.That(workflow, Does.Contain("Start Menu shortcut remained after uninstall."));
                Assert.That(workflow, Does.Contain("Start Menu uninstall shortcut remained after uninstall."));
                Assert.That(workflow, Does.Contain("Autostart registry value remained after uninstall."));
                Assert.That(workflow, Does.Contain("Upload desktop Windows installer evidence"));
                Assert.That(workflow, Does.Contain("name: desktop-windows-installer-evidence"));
                Assert.That(workflow, Does.Contain("path: ${{ runner.temp }}/cotton-sync-vfs-release-evidence"));
                Assert.That(workflow, Does.Contain("if-no-files-found: error"));
                Assert.That(workflow, Does.Contain("retention-days: 14"));
            });
        }
    }
}
