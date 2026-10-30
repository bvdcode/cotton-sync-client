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
                Assert.That(Regex.Matches(installerScript, "Flags: deletekey dontcreatekey").Count, Is.EqualTo(2));
                Assert.That(installerScript, Does.Not.Contain("Copy Cotton Cloud share link"));
                Assert.That(installerScript, Does.Not.Contain("--copy-shell-share-link"));
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

    }
}
