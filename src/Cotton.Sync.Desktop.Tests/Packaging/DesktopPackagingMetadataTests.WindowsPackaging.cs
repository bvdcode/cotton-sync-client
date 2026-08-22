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
        public void WindowsChecksumVerificationScript_VerifiesPublishedManifest()
        {
            string checksumScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-checksums.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(checksumScript, Does.Contain("[string]$PublishDirectory"));
                Assert.That(checksumScript, Does.Contain("checksums.sha256"));
                Assert.That(checksumScript, Does.Contain("Get-FileHash -Algorithm SHA256"));
                Assert.That(checksumScript, Does.Contain("Checksum mismatch"));
                Assert.That(checksumScript, Does.Contain("No publish checksums were verified."));
            });
        }

        [Test]
        public void WindowsVirtualFilesPackaging_UsesOsCloudFilesApiInNonTrimmedWindowsPublish()
        {
            XDocument profile = XDocument.Load(GetPublishProfilePath("win-x64"));
            XDocument desktopProject = XDocument.Load(GetDesktopProjectPath());
            XDocument windowsShellProject = XDocument.Load(GetWindowsShellProjectPath());
            XElement propertyGroup = profile.Root!.Elements("PropertyGroup").Single();
            XElement windowsShellPropertyGroup = windowsShellProject.Root!.Elements("PropertyGroup").Single();
            string workflow = GetDesktopWorkflow();
            string solution = File.ReadAllText(GetRepositoryFilePath(Path.Combine("src", "Cotton.sln")));
            string installerScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/cotton-sync.iss"));
            string nativeApiSource = File.ReadAllText(GetDesktopFilePath(Path.Combine("Platform", "WindowsCloudFilesNativeApi.cs")));
            string nativeApiSource = File.ReadAllText(
                GetDesktopFilePath("Platform/WindowsCloudFilesNativeApi.Imports.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApiType.Assembly.GetName().Name, Is.EqualTo("Cotton.Sync.Desktop"));
                Assert.That(GetProperty(propertyGroup, "RuntimeIdentifier"), Is.EqualTo("win-x64"));
                Assert.That(GetProperty(propertyGroup, "SelfContained"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroup, "UseAppHost"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroup, "PublishSingleFile"), Is.EqualTo("false"));
                Assert.That(GetProperty(propertyGroup, "PublishTrimmed"), Is.EqualTo("false"));
                Assert.That(GetProperty(propertyGroup, "PublishReadyToRun"), Is.EqualTo("false"));
                Assert.That(
                    workflow,
                    Does.Contain("dotnet publish src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj --no-restore /p:PublishProfile=win-x64"));
                Assert.That(workflow, Does.Not.Contain("    paths:"));
                Assert.That(solution, Does.Contain(@"Cotton.Sync.WindowsShell\Cotton.Sync.WindowsShell.csproj"));
                Assert.That(
                    GetProperty(windowsShellPropertyGroup, "TargetFramework"),
                    Is.EqualTo("net10.0-windows10.0.19041.0"));
                Assert.That(GetProperty(windowsShellPropertyGroup, "SelfContained"), Is.EqualTo("true"));
                Assert.That(GetProperty(windowsShellPropertyGroup, "PublishSingleFile"), Is.EqualTo("true"));
                Assert.That(GetProperty(windowsShellPropertyGroup, "PublishTrimmed"), Is.EqualTo("false"));
                Assert.That(
                    desktopProject.Root!.Elements("Target").Any(static target =>
                        string.Equals(target.Attribute("Name")?.Value, "PublishWindowsShellHelper", StringComparison.Ordinal)
                        && target.ToString().Contains("Cotton.Sync.WindowsShell.csproj", StringComparison.Ordinal)
                        && target.ToString().Contains("WindowsShell", StringComparison.Ordinal)),
                    Is.True);
                Assert.That(
                    desktopProject.Root!.Elements("Target").Single(static target =>
                            string.Equals(target.Attribute("Name")?.Value, "GeneratePublishChecksums", StringComparison.Ordinal))
                        .Attribute("DependsOnTargets")?.Value,
                    Does.Contain("PublishWindowsShellHelper"));
                Assert.That(installerScript, Does.Contain("Source: \"{#SourceDir}\\*\""));
                Assert.That(installerScript, Does.Contain("recursesubdirs createallsubdirs"));
                Assert.That(installerScript, Does.Not.Contain("[UninstallRun]"));
                Assert.That(installerScript, Does.Contain("RunCloudFilesCleanupForUninstall"));
                Assert.That(installerScript, Does.Contain("--cleanup-cloud-files"));
                Assert.That(installerScript, Does.Contain("$deadline = (Get-Date).AddSeconds(60)"));
                Assert.That(installerScript, Does.Contain("Cloud Files cleanup timed out."));
                Assert.That(installerScript, Does.Contain("Cloud Files cleanup command exited with code %d."));
                Assert.That(workflow, Does.Contain("Cloud Files cleanup did not exit cleanly during uninstall."));
                Assert.That(workflow, Does.Contain("Cloud Files cleanup did not exit cleanly during reinstall cleanup."));
                AssertCloudFilesImport(nativeApiSource, "CfRegisterSyncRoot");
                AssertCloudFilesImport(nativeApiSource, "CfUnregisterSyncRoot");
                AssertCloudFilesImport(nativeApiSource, "CfCreatePlaceholders");
                AssertCloudFilesImport(nativeApiSource, "CfConnectSyncRoot");
                AssertCloudFilesImport(nativeApiSource, "CfDisconnectSyncRoot");
                AssertCloudFilesImport(nativeApiSource, "CfSetPinState");
                AssertCloudFilesImport(nativeApiSource, "CfConvertToPlaceholder");
                AssertCloudFilesImport(nativeApiSource, "CfExecute");
                AssertCloudFilesImport(nativeApiSource, "CfOpenFileWithOplock");
                AssertCloudFilesImport(nativeApiSource, "CfDehydratePlaceholder");
                AssertCloudFilesImport(nativeApiSource, "CfUpdatePlaceholder");
                AssertCloudFilesImport(nativeApiSource, "CfCloseHandle");
                Assert.That(nativeApiSource, Does.Contain("AutoDehydrationAllowed"));
            });
        }

        [Test]
        public void CiWorkflow_BuildsAndUploadsLinuxDebArtifact()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Package desktop Linux x64 deb"));
                Assert.That(workflow, Does.Contain("src/Cotton.Sync.Desktop/Packaging/linux/package-deb.sh"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.deb"));
                Assert.That(
                    Regex.Matches(workflow, "cotton-sync-desktop-linux-x64-\\$\\{\\{ steps\\.gitversion\\.outputs\\.SemVer \\}\\}\\.deb").Count,
                    Is.GreaterThanOrEqualTo(2));
            });
        }

        [Test]
        public void CiWorkflow_CapturesLinuxGuiScreenshot()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("ffmpeg gnome-keyring libnotify-bin libsecret-tools x11-apps x11-utils xauth xvfb"));
                Assert.That(workflow, Does.Contain("command -v xprop"));
                Assert.That(workflow, Does.Contain("command -v notify-send"));
                Assert.That(workflow, Does.Contain("command -v xwd"));
                Assert.That(workflow, Does.Contain("command -v xwininfo"));
                Assert.That(workflow, Does.Contain("Smoke desktop Linux GUI screenshot"));
                Assert.That(workflow, Does.Contain("xvfb-run -a -s \"-screen 0 1024x768x24\""));
                Assert.That(workflow, Does.Contain("Packaging/linux/smoke-gui-screenshot-matrix.sh"));
                Assert.That(workflow, Does.Contain("Upload desktop Linux GUI screenshot"));
                Assert.That(workflow, Does.Contain("name: desktop-linux-gui-screenshot"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-linux-*.png"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-linux-*.png.log"));
            });
        }

        [Test]
        public void CiWorkflow_SmokesLinuxPackageArtifacts()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Smoke desktop Linux archive artifact"));
                Assert.That(workflow, Does.Contain("tar -xzf cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.tar.gz"));
                Assert.That(workflow, Does.Contain("self_test_timeout=120s"));
                Assert.That(workflow, Does.Contain("xvfb-run -a -s \"-screen 0 1024x768x24\""));
                Assert.That(workflow, Does.Contain("timeout \"$self_test_timeout\""));
                Assert.That(workflow, Does.Contain("\"$extract_dir/Cotton.Sync.Desktop\" --self-test --data-dir"));
                Assert.That(workflow, Does.Contain("Packaging/linux/verify-checksums.sh"));
                Assert.That(workflow, Does.Contain("Packaging/linux/smoke-diagnostics-export.sh"));
                Assert.That(workflow, Does.Contain("Smoke desktop Linux deb artifact"));
                Assert.That(workflow, Does.Contain("dpkg-deb -x cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.deb"));
                Assert.That(workflow, Does.Contain("test -f \"$extract_dir/usr/share/applications/cotton-sync.desktop\""));
                Assert.That(workflow, Does.Contain("test -f \"$extract_dir/usr/share/icons/hicolor/192x192/apps/cotton-sync.png\""));
                Assert.That(workflow, Does.Contain("test -L \"$extract_dir/usr/bin/cotton-sync\""));
                Assert.That(workflow, Does.Contain("\"$extract_dir/opt/cotton-sync\""));
                Assert.That(workflow, Does.Contain("\"$extract_dir/opt/cotton-sync/Cotton.Sync.Desktop\" --self-test --data-dir"));
            });
        }

        [Test]
        public void CiWorkflow_SmokesLinuxDebInstallAndUninstall()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Smoke desktop Linux deb install"));
                Assert.That(workflow, Does.Contain("sudo dpkg -i cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.deb"));
                Assert.That(workflow, Does.Contain("sudo dpkg -r cotton-sync-desktop"));
                Assert.That(workflow, Does.Contain("test -x /opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(workflow, Does.Contain("test -L /usr/bin/cotton-sync"));
                Assert.That(workflow, Does.Contain("Packaging/linux/verify-checksums.sh /opt/cotton-sync"));
                Assert.That(workflow, Does.Contain("/opt/cotton-sync/Cotton.Sync.Desktop --self-test --data-dir"));
                Assert.That(workflow, Does.Contain("Packaging/linux/smoke-diagnostics-export.sh"));
                Assert.That(workflow, Does.Contain("test ! -e /opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(workflow, Does.Contain("test ! -e /usr/bin/cotton-sync"));
                Assert.That(workflow, Does.Contain("$HOME/.config/autostart/cotton-sync.desktop"));
                Assert.That(workflow, Does.Contain("Exec=/opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(workflow, Does.Contain("test ! -e \"$HOME/.config/autostart/cotton-sync.desktop\""));
            });
        }

        [Test]
        public void CiWorkflow_SmokesLinuxDebUpgrade()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Smoke desktop Linux deb upgrade"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-linux-x64-old.deb"));
                Assert.That(workflow, Does.Contain("0.0.1-ci-upgrade"));
                Assert.That(workflow, Does.Contain("sudo dpkg -i \"$old_deb\""));
                Assert.That(workflow, Does.Contain("sudo dpkg -i cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.deb"));
                Assert.That(workflow, Does.Contain("dpkg-query -W -f='${Version}' cotton-sync-desktop"));
                Assert.That(workflow, Does.Contain("Expected upgraded package version"));
                Assert.That(workflow, Does.Contain("Packaging/linux/verify-checksums.sh /opt/cotton-sync"));
                Assert.That(workflow, Does.Contain("/opt/cotton-sync/Cotton.Sync.Desktop --self-test --data-dir"));
                Assert.That(workflow, Does.Contain("Packaging/linux/smoke-diagnostics-export.sh"));
                Assert.That(workflow, Does.Contain("$HOME/.config/autostart/cotton-sync.desktop"));
                Assert.That(workflow, Does.Contain("Exec=/opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(workflow, Does.Contain("sudo dpkg -r cotton-sync-desktop"));
                Assert.That(workflow, Does.Contain("test ! -e /opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(workflow, Does.Contain("test ! -e /usr/bin/cotton-sync"));
                Assert.That(workflow, Does.Contain("test ! -e \"$HOME/.config/autostart/cotton-sync.desktop\""));
            });
        }

        [Test]
        public void CiWorkflow_RunsWindowsDesktopSmoke()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("windows:"));
                Assert.That(workflow, Does.Contain("Desktop Windows Package Smoke"));
                Assert.That(workflow, Does.Contain("runs-on: windows-latest"));
                Assert.That(workflow, Does.Contain("/p:PublishProfile=win-x64"));
                Assert.That(workflow, Does.Contain("-p:Version='${{ steps.gitversion.outputs.SemVer }}'"));
                Assert.That(workflow, Does.Contain("-p:AssemblyVersion='${{ steps.gitversion.outputs.AssemblyVersion }}'"));
                Assert.That(workflow, Does.Contain("-p:FileVersion='${{ steps.gitversion.outputs.FileVersion }}'"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-associated-icon.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedIcon \"src/Cotton.Sync.Desktop/Assets/app.ico\""));
                Assert.That(workflow, Does.Contain("Cotton.Sync.Desktop.exe --self-test --data-dir"));
            });
        }

        [Test]
        public void WindowsAssociatedIconVerifier_ComparesPublishedExeWithAppIcon()
        {
            string iconScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-associated-icon.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(iconScript, Does.Contain("[System.Drawing.Icon]::ExtractAssociatedIcon"));
                Assert.That(iconScript, Does.Contain("$expectedDesktopIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolvedIcon)"));
                Assert.That(iconScript, Does.Contain("[System.Security.Cryptography.SHA256]::Create()"));
                Assert.That(iconScript, Does.Contain("ComputeHash($bytes)"));
                Assert.That(iconScript, Does.Contain("Desktop executable associated icon does not match"));
                Assert.That(iconScript, Does.Contain("Verified Windows associated icon"));
            });
        }

        [Test]
        public void WindowsShortcutAppIdVerifier_ReadsShortcutAppUserModelId()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-shortcut-app-id.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("New-Object -ComObject Shell.Application"));
                Assert.That(script, Does.Contain("System.AppUserModel.ID"));
                Assert.That(script, Does.Contain("ExpectedAppUserModelId"));
                Assert.That(script, Does.Contain("Windows shortcut AppUserModelID"));
                Assert.That(script, Does.Contain("Verified Windows shortcut AppUserModelID"));
            });
        }

        [Test]
        public void WindowsNotificationIdentitySmokeScript_VerifiesInstalledSenderIdentity()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-notification-identity.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataDirectory"));
                Assert.That(script, Does.Contain("--export-diagnostics"));
                Assert.That(script, Does.Contain("diagnostics.json"));
                Assert.That(script, Does.Contain("$diagnostics.notification"));
                Assert.That(script, Does.Contain("isDeliveryExecutableAvailable"));
                Assert.That(script, Does.Contain("isInstalledAppIdentityVerified"));
                Assert.That(script, Does.Contain("installed-sender-identity"));
                Assert.That(script, Does.Contain("Notification adapter"));
                Assert.That(script, Does.Contain("Notification adapter diagnostics item did not pass."));
                Assert.That(script, Does.Contain("Notification adapter diagnostics item was skipped."));
            });
        }

        [Test]
        public void WindowsCloudFilesSelfTestTruthfulnessSmokeScript_CrossChecksSelfTestAgainstVfsSmoke()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-cloud-files-self-test-truthfulness.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$AppExecutable"));
                Assert.That(script, Does.Contain("[string]$DataDirectory"));
                Assert.That(script, Does.Contain("[string]$VfsSmokeDataDirectory = \"\""));
                Assert.That(script, Does.Contain("[string]$LocalRoot = \"S:\\CottonSyncVfsQa\\root\""));
                Assert.That(script, Does.Contain("[string[]]$AdditionalVfsSmokePhases = @()"));
                Assert.That(script, Does.Contain("[int]$InitialStreamingPlaceholderCount = 100000"));
                Assert.That(script, Does.Contain("[int]$SteadyStateRepeatPlaceholderCount = 100000"));
                Assert.That(script, Does.Contain("--self-test"));
                Assert.That(script, Does.Contain("--windows-virtual-files-smoke"));
                Assert.That(script, Does.Contain("--local-root"));
                Assert.That(script, Does.Contain("--vfs-smoke-phase"));
                Assert.That(script, Does.Contain("InitialStreamingPlaceholderCount must be greater than zero."));
                Assert.That(script, Does.Contain("initial-streaming-logging"));
                Assert.That(script, Does.Contain("steady-state-repeat"));
                Assert.That(script, Does.Contain("Starting Windows virtual files smoke phase '$phaseName'."));
                Assert.That(script, Does.Contain("Completed Windows virtual files smoke phase '$phaseName'."));
                Assert.That(script, Does.Contain("--vfs-smoke-placeholder-count"));
                Assert.That(script, Does.Contain("SteadyStateRepeatPlaceholderCount must be greater than zero."));
                Assert.That(script, Does.Contain("Additional Windows virtual files smoke phase '$phaseName' failed."));
                Assert.That(script, Does.Contain("'^\\[(OK|SKIP|FAIL)\\] Windows virtual files - '"));
                Assert.That(script, Does.Contain("Windows virtual files self-test reported OK even though the VFS smoke failed."));
                Assert.That(script, Does.Contain("Windows virtual files self-test reported '$windowsVirtualFilesStatus' even though the VFS smoke passed."));
                Assert.That(script, Does.Contain("Verified Cloud Files self-test truthfulness"));
            });
        }

        [Test]
        public void WindowsCloudFilesCleanupVerifierScript_ChecksExplorerAndStorageProviderRegistrations()
        {
            string script = File.ReadAllText(GetDesktopFilePath("Packaging/windows/verify-cloud-files-cleanup.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("Cotton.Sync.Desktop"));
                Assert.That(script, Does.Contain("Cotton Cloud"));
                Assert.That(script, Does.Contain("Cotton Sync"));
                Assert.That(
                    script,
                    Does.Contain("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\SyncRootManager"));
                Assert.That(
                    script,
                    Does.Contain("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Desktop\\NameSpace"));
                Assert.That(script, Does.Contain("Software\\Classes\\CLSID"));
                Assert.That(script, Does.Contain("Software\\Classes\\WOW6432Node\\CLSID"));
                Assert.That(script, Does.Contain("[string]$ReportPath = \"\""));
                Assert.That(script, Does.Contain("Write-CleanupReport"));
                Assert.That(script, Does.Contain("[AllowEmptyCollection()]"));
                Assert.That(script, Does.Contain("$checkedScopes = @("));
                Assert.That(script, Does.Contain("CheckedScope: $scope"));
                Assert.That(script, Does.Contain("RemainingRegistrationCount: $($Registrations.Count)"));
                Assert.That(script, Does.Contain("Write-CleanupReport -Result \"passed\""));
                Assert.That(script, Does.Contain("Test-ShellNamespaceRoots"));
                Assert.That(script, Does.Contain("Test-ClassIdRoots"));
                Assert.That(script, Does.Contain("Cloud Files or Explorer registration remained after uninstall."));
                Assert.That(
                    script,
                    Does.Contain("Verified Cloud Files and Explorer registrations were removed after uninstall."));
            });
        }

        [Test]
        public void CiWorkflow_SmokesWindowsZipArchiveOnWindows()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Setup Python"));
                Assert.That(workflow, Does.Contain("Smoke desktop Windows zip archive"));
                Assert.That(workflow, Does.Contain("Packaging/windows/package-zip.py"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-checksums.ps1"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-associated-icon.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(workflow, Does.Contain("Expand-Archive cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(workflow, Does.Contain("Cotton.Sync.Desktop.exe\") --self-test --data-dir"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-diagnostics-export.ps1"));
                Assert.That(workflow, Does.Contain("-AppExecutable (Join-Path $extractDir \"Cotton.Sync.Desktop.exe\")"));
            });
        }

        [Test]
        public void CiWorkflow_UploadsWindowsZipPortableArtifact()
        {
            string workflow = GetDesktopWorkflow();
            string packageScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/package-zip.py"));

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Package desktop Windows x64 zip"));
                Assert.That(workflow, Does.Contain("src/Cotton.Sync.Desktop/Packaging/windows/package-zip.py"));
                Assert.That(workflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(packageScript, Does.Contain("Cotton.Sync.Desktop.exe"));
                Assert.That(packageScript, Does.Contain("checksums.sha256"));
                Assert.That(packageScript, Does.Contain("ZipFile(output_zip, \"w\", ZIP_DEFLATED)"));
                Assert.That(packageScript, Does.Contain("path.relative_to(resolved_publish_dir).as_posix()"));
                Assert.That(
                    Regex.Matches(workflow, "cotton-sync-desktop-win-x64-\\$\\{\\{ steps\\.gitversion\\.outputs\\.SemVer \\}\\}\\.zip").Count,
                    Is.GreaterThanOrEqualTo(2));
            });
        }

    }
}
