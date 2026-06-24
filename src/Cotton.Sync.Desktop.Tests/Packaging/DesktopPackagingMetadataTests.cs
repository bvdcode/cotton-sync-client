// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Packaging
{
    public class DesktopPackagingMetadataTests
    {
        [Test]
        public void DesktopProject_DefinesWindowsAndLinuxReleaseMetadata()
        {
            XDocument project = XDocument.Load(GetDesktopProjectPath());
            XElement propertyGroup = project.Root!.Elements("PropertyGroup").First();

            Assert.Multiple(() =>
            {
                Assert.That(GetProperty(propertyGroup, "UseAppHost"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroup, "Title"), Is.EqualTo("Cotton Sync"));
                Assert.That(GetProperty(propertyGroup, "Product"), Is.EqualTo("Cotton Sync"));
                Assert.That(GetProperty(propertyGroup, "ApplicationIcon"), Is.EqualTo("Assets/app.ico"));
                Assert.That(GetProperty(propertyGroup, "Win32Icon"), Is.EqualTo("Assets/app.ico"));
                Assert.That(
                    GetProperty(propertyGroup, "RuntimeIdentifiers")?.Split(';'),
                    Is.EquivalentTo(new[] { "win-x64", "linux-x64" }));
            });
        }

        [TestCaseSource(nameof(VersionedApplicationProjectPaths))]
        public void ApplicationProject_DoesNotHardCodeGeneratedReleaseVersionMetadata(string projectPath)
        {
            XDocument project = XDocument.Load(projectPath);
            XElement propertyGroup = project.Root!.Elements("PropertyGroup").First();

            Assert.Multiple(() =>
            {
                Assert.That(GetProperty(propertyGroup, "VersionPrefix"), Is.EqualTo("0.1.0"));
                Assert.That(GetProperty(propertyGroup, "AssemblyVersion"), Is.Null);
                Assert.That(GetProperty(propertyGroup, "FileVersion"), Is.Null);
                Assert.That(GetProperty(propertyGroup, "InformationalVersion"), Is.Null);
            });
        }

        [Test]
        public void DesktopProject_PublishesWindowsShellHelperWithReleaseVersionMetadata()
        {
            string project = File.ReadAllText(GetDesktopProjectPath());

            Assert.Multiple(() =>
            {
                Assert.That(project, Does.Contain("CottonWindowsShellHelperVersionProperties"));
                Assert.That(project, Does.Contain("-p:Version=&quot;$(Version)&quot;"));
                Assert.That(project, Does.Contain("-p:AssemblyVersion=&quot;$(AssemblyVersion)&quot;"));
                Assert.That(project, Does.Contain("-p:FileVersion=&quot;$(FileVersion)&quot;"));
                Assert.That(project, Does.Contain("-p:InformationalVersion=&quot;$(Version)&quot;"));
            });
        }

        [Test]
        public void WindowsShellHelper_SingleUnregisterCleansOrphanedShellNamespaceRoot()
        {
            string program = File.ReadAllText(GetWindowsShellProgramPath());

            Assert.Multiple(() =>
            {
                Assert.That(program, Does.Contain("RemoveOrphanedShellNamespaceRoot(syncRootId, cleanupTargetFolderPath)"));
                Assert.That(program, Does.Contain("unregister <account> [root]"));
                Assert.That(program, Does.Contain("args.Length is 2 or 3"));
                Assert.That(program, Does.Contain("ClassIdTargetsFolderPath"));
                Assert.That(program, Does.Contain("ShellNamespaceRootMatches"));
                Assert.That(program, Does.Contain("RemoveClassIdSubKeysForTargetFolderPath(cleanupTargetFolderPath)"));
                Assert.That(program, Does.Contain("\"TargetFolderPath\""));
                Assert.That(program, Does.Contain("\"unregister shell-namespace=\""));
                Assert.That(program, Does.Contain("\" class-id=\""));
                Assert.That(program, Does.Contain("StorageProviderSyncRootManager.IsSupported()"));
            });
        }

        [TestCase("win-x64")]
        [TestCase("linux-x64")]
        public void PublishProfile_DefinesSelfContainedPortableArtifact(string runtimeIdentifier)
        {
            XDocument profile = XDocument.Load(GetPublishProfilePath(runtimeIdentifier));
            XElement propertyGroup = profile.Root!.Elements("PropertyGroup").Single();

            Assert.Multiple(() =>
            {
                Assert.That(GetProperty(propertyGroup, "PublishProtocol"), Is.EqualTo("FileSystem"));
                Assert.That(GetProperty(propertyGroup, "Configuration"), Is.EqualTo("Release"));
                Assert.That(GetProperty(propertyGroup, "TargetFramework"), Is.EqualTo("net10.0"));
                Assert.That(GetProperty(propertyGroup, "RuntimeIdentifier"), Is.EqualTo(runtimeIdentifier));
                Assert.That(GetProperty(propertyGroup, "SelfContained"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroup, "UseAppHost"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroup, "PublishSingleFile"), Is.EqualTo("false"));
                Assert.That(GetProperty(propertyGroup, "PublishTrimmed"), Is.EqualTo("false"));
                Assert.That(GetProperty(propertyGroup, "PublishReadyToRun"), Is.EqualTo("false"));
                Assert.That(NormalizeProfilePath(GetProperty(propertyGroup, "PublishDir")), Does.EndWith("/publish/" + runtimeIdentifier + "/"));
            });
        }

        [Test]
        public void DesktopProject_CopiesLinuxDesktopEntryOnlyForLinuxPublish()
        {
            XDocument project = XDocument.Load(GetDesktopProjectPath());
            XElement content = project.Root!
                .Elements("ItemGroup")
                .Single(static itemGroup => string.Equals(
                    itemGroup.Attribute("Condition")?.Value,
                    "'$(RuntimeIdentifier)' == 'linux-x64'",
                    StringComparison.Ordinal))
                .Elements("Content")
                .Single();

            Assert.Multiple(() =>
            {
                Assert.That(
                    content.Attribute("Include")?.Value,
                    Is.EqualTo("Packaging/linux/cotton-sync.desktop"));
                Assert.That(content.Attribute("Link")?.Value, Is.EqualTo("cotton-sync.desktop"));
                Assert.That(content.Attribute("CopyToPublishDirectory")?.Value, Is.EqualTo("PreserveNewest"));
            });
        }

        [Test]
        public void DesktopProject_CleansPublishDirectoryBeforePublishing()
        {
            XDocument project = XDocument.Load(GetDesktopProjectPath());
            XElement target = project.Root!
                .Elements("Target")
                .Single(static element => string.Equals(
                    element.Attribute("Name")?.Value,
                    "CleanDesktopPublishDirectory",
                    StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(target.Attribute("BeforeTargets")?.Value, Is.EqualTo("PrepareForPublish"));
                Assert.That(target.Attribute("Condition")?.Value, Does.Contain("Exists('$(PublishDir)')"));
                Assert.That(
                    target.Elements("RemoveDir").Single().Attribute("Directories")?.Value,
                    Is.EqualTo("$(PublishDir)"));
            });
        }

        [Test]
        public void DesktopProject_GeneratesChecksumsWithPublishRelativePaths()
        {
            XDocument project = XDocument.Load(GetDesktopProjectPath());
            XElement target = project.Root!
                .Elements("Target")
                .Single(static element => string.Equals(
                    element.Attribute("Name")?.Value,
                    "GeneratePublishChecksums",
                    StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(target.ToString(), Does.Contain("CottonPublishDir"));
                Assert.That(target.ToString(), Does.Contain("AssignTargetPath"));
                Assert.That(target.ToString(), Does.Contain("ManifestPath"));
                Assert.That(target.ToString(), Does.Contain("RootFolder=\"$(CottonPublishDir)\""));
                Assert.That(target.ToString(), Does.Contain("%(FileHash)  %(ManifestPath)"));
                Assert.That(target.ToString(), Does.Not.Contain("%(RecursiveDir)%(Filename)%(Extension)"));
            });
        }

        [Test]
        public void LinuxDesktopEntry_DefinesLauncherMetadata()
        {
            string desktopEntry = File.ReadAllText(GetDesktopFilePath("Packaging/linux/cotton-sync.desktop"));

            Assert.Multiple(() =>
            {
                Assert.That(desktopEntry, Does.Contain("[Desktop Entry]"));
                Assert.That(desktopEntry, Does.Contain("Type=Application"));
                Assert.That(desktopEntry, Does.Contain("Name=Cotton Sync"));
                Assert.That(desktopEntry, Does.Contain("Exec=Cotton.Sync.Desktop"));
                Assert.That(desktopEntry, Does.Contain("TryExec=Cotton.Sync.Desktop"));
                Assert.That(desktopEntry, Does.Contain("Icon=cotton-sync"));
                Assert.That(desktopEntry, Does.Contain("Terminal=false"));
                Assert.That(desktopEntry, Does.Contain("Categories=Network;FileTransfer;"));
                Assert.That(desktopEntry, Does.Contain("StartupWMClass=Cotton.Sync.Desktop"));
            });
        }

        [Test]
        public void LinuxDebPackageScript_DefinesReleaseInstallLayout()
        {
            string packageScript = File.ReadAllText(GetDesktopFilePath("Packaging/linux/package-deb.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(packageScript, Does.Contain("/opt/cotton-sync"));
                Assert.That(packageScript, Does.Contain("/usr/bin/cotton-sync"));
                Assert.That(packageScript, Does.Contain("/usr/share/applications/cotton-sync.desktop"));
                Assert.That(packageScript, Does.Contain("/usr/share/icons/hicolor/192x192/apps/cotton-sync.png"));
                Assert.That(packageScript, Does.Not.Contain("rm -f \"$package_root/opt/cotton-sync/cotton-sync.desktop\""));
                Assert.That(packageScript, Does.Contain("checksums.sha256"));
                Assert.That(packageScript, Does.Contain("Package: cotton-sync-desktop"));
                Assert.That(packageScript, Does.Contain("cat > \"$package_root/DEBIAN/postrm\""));
                Assert.That(packageScript, Does.Contain("cleanup_autostart_file"));
                Assert.That(packageScript, Does.Contain("Name=Cotton Sync"));
                Assert.That(packageScript, Does.Contain("Exec=/opt/cotton-sync/Cotton.Sync.Desktop"));
                Assert.That(packageScript, Does.Contain("chmod 755 \"$package_root/DEBIAN/postrm\""));
                Assert.That(packageScript, Does.Contain("Architecture: amd64"));
                Assert.That(packageScript, Does.Contain("Depends: libnotify-bin, libsecret-tools"));
                Assert.That(packageScript, Does.Contain("dpkg-deb --root-owner-group --build"));
            });
        }

        [Test]
        public void LinuxGuiScreenshotSmokeScript_CapturesPublishedAppWindow()
        {
            string smokeScript = File.ReadAllText(GetDesktopFilePath("Packaging/linux/smoke-gui-screenshot.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(smokeScript, Does.Contain("[app-args...]"));
                Assert.That(smokeScript, Does.Contain("shift 2"));
                Assert.That(smokeScript, Does.Contain("DISPLAY is required"));
                Assert.That(smokeScript, Does.Contain("command -v ffmpeg"));
                Assert.That(smokeScript, Does.Contain("command -v ffprobe"));
                Assert.That(smokeScript, Does.Contain("command -v xprop"));
                Assert.That(smokeScript, Does.Contain("command -v xwininfo"));
                Assert.That(smokeScript, Does.Not.Contain("command -v xwd"));
                Assert.That(smokeScript, Does.Contain("\"$app_executable\" --data-dir \"$data_dir\" \"$@\""));
                Assert.That(smokeScript, Does.Contain("xprop -id \"$window_id\" _NET_WM_PID"));
                Assert.That(smokeScript, Does.Contain("xwininfo -root -tree"));
                Assert.That(smokeScript, Does.Contain("0x[0-9a-fA-F]+"));
                Assert.That(smokeScript, Does.Not.Contain("awk '/\"Cotton Sync\"/"));
                Assert.That(smokeScript, Does.Contain("dump_window_tree()"));
                Assert.That(smokeScript, Does.Contain("X11 window tree at failure:"));
                Assert.That(smokeScript, Does.Contain("Desktop app window was not found for process"));
                Assert.That(smokeScript, Does.Contain("get_window_size()"));
                Assert.That(smokeScript, Does.Contain("Could not detect desktop app window size."));
                Assert.That(smokeScript, Does.Contain("get_window_origin()"));
                Assert.That(smokeScript, Does.Contain("Could not detect desktop app window origin."));
                Assert.That(smokeScript, Does.Contain("resize_app_window_if_requested()"));
                Assert.That(smokeScript, Does.Contain("COTTON_SYNC_SCREENSHOT_WINDOW_SIZE"));
                Assert.That(smokeScript, Does.Contain("must use WIDTHxHEIGHT"));
                Assert.That(smokeScript, Does.Contain("wmctrl -ir \"$app_window_id\" -e"));
                Assert.That(smokeScript, Does.Contain("wmctrl -ia \"$app_window_id\""));
                Assert.That(smokeScript, Does.Contain("-f x11grab"));
                Assert.That(smokeScript, Does.Contain("-video_size \"$capture_size\""));
                Assert.That(smokeScript, Does.Contain("-i \"${DISPLAY}+${capture_origin}\""));
                Assert.That(smokeScript, Does.Contain("-frames:v 1"));
                Assert.That(smokeScript, Does.Contain("Desktop app exited during screenshot capture."));
                Assert.That(smokeScript, Does.Contain("TypeLoadException"));
                Assert.That(smokeScript, Does.Contain("Desktop app log contains runtime exception signatures."));
                Assert.That(smokeScript, Does.Contain("GUI screenshot was not created"));
                Assert.That(smokeScript, Does.Contain("ffprobe -v error"));
                Assert.That(smokeScript, Does.Contain("expected app window $capture_size"));
                Assert.That(smokeScript, Does.Contain("lavfi.signalstats.YMIN"));
                Assert.That(smokeScript, Does.Contain("COTTON_SYNC_SCREENSHOT_CAPTURE_ATTEMPTS"));
                Assert.That(smokeScript, Does.Contain("capture attempt $attempt produced a single-color frame; retrying."));
                Assert.That(smokeScript, Does.Contain("All $capture_attempts screenshot capture attempt(s) were single-color frames."));
                Assert.That(smokeScript, Does.Contain("GUI screenshot appears to be a single-color frame."));
                Assert.That(smokeScript, Does.Contain("Captured desktop GUI screenshot"));
            });
        }

        [Test]
        public void LinuxGuiScreenshotMatrixScript_CapturesDefaultVisualSmokeStates()
        {
            string smokeScript = File.ReadAllText(GetDesktopFilePath("Packaging/linux/smoke-gui-screenshot-matrix.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(smokeScript, Does.Contain("Usage: smoke-gui-screenshot-matrix.sh <app-executable> <output-dir> [scenario...]"));
                Assert.That(smokeScript, Does.Contain("DISPLAY is required"));
                Assert.That(smokeScript, Does.Contain("set -- sign-in-error empty-dashboard add-folder dashboard folder-controls progress settings settings-diagnostics error conflict"));
                Assert.That(smokeScript, Does.Contain("smoke-gui-screenshot.sh"));
                Assert.That(smokeScript, Does.Contain("cotton-sync-desktop-linux-gui.png"));
                Assert.That(smokeScript, Does.Contain("cotton-sync-desktop-linux-${scenario}.png"));
                Assert.That(smokeScript, Does.Contain("--visual-smoke \"$scenario\""));
            });
        }

        [Test]
        public void LinuxDiagnosticsExportSmokeScript_VerifiesBundleMetadata()
        {
            string smokeScript = File.ReadAllText(GetDesktopFilePath("Packaging/linux/smoke-diagnostics-export.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(smokeScript, Does.Contain("Usage: $0 <app-executable> <data-dir>"));
                Assert.That(smokeScript, Does.Contain("--export-diagnostics --data-dir"));
                Assert.That(smokeScript, Does.Contain("command -v timeout"));
                Assert.That(smokeScript, Does.Contain("COTTON_SYNC_DIAGNOSTICS_TIMEOUT"));
                Assert.That(smokeScript, Does.Contain("Diagnostics export timed out after"));
                Assert.That(smokeScript, Does.Contain("Diagnostics export exited with code"));
                Assert.That(smokeScript, Does.Contain("sed -n 's/^Bundle: //p'"));
                Assert.That(smokeScript, Does.Contain("Diagnostics bundle path was not reported."));
                Assert.That(smokeScript, Does.Contain("Diagnostics bundle was not created at $bundle_path."));
                Assert.That(smokeScript, Does.Contain("command -v python3"));
                Assert.That(smokeScript, Does.Contain("diagnostics.json"));
                Assert.That(smokeScript, Does.Contain("\"dataPaths\""));
                Assert.That(smokeScript, Does.Contain("\"[data-directory]\""));
                Assert.That(smokeScript, Does.Contain("\"[app-database]\""));
                Assert.That(smokeScript, Does.Contain("\"[sync-state-database]\""));
                Assert.That(smokeScript, Does.Contain("\"[token-store]\""));
                Assert.That(smokeScript, Does.Contain("Public diagnostics JSON leaked private path value"));
                Assert.That(smokeScript, Does.Contain("\"sync-app.db\""));
                Assert.That(smokeScript, Does.Contain("\"sync-state.db\""));
                Assert.That(smokeScript, Does.Contain("\"tokens.json\""));
                Assert.That(smokeScript, Does.Contain("Verified diagnostics bundle metadata:"));
                Assert.That(smokeScript, Does.Contain("Exported diagnostics bundle:"));
            });
        }

        [Test]
        public void LinuxChecksumVerificationScript_VerifiesPublishedManifest()
        {
            string checksumScript = File.ReadAllText(GetDesktopFilePath("Packaging/linux/verify-checksums.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(checksumScript, Does.Contain("Usage: verify-checksums.sh <publish-dir>"));
                Assert.That(checksumScript, Does.Contain("checksums.sha256"));
                Assert.That(checksumScript, Does.Contain("sha256sum -c checksums.sha256"));
                Assert.That(checksumScript, Does.Contain("Verified publish checksums"));
            });
        }

        [Test]
        public void WindowsDiagnosticsExportSmokeScript_VerifiesBundleMetadata()
        {
            string smokeScript = File.ReadAllText(GetDesktopFilePath("Packaging/windows/smoke-diagnostics-export.ps1"));

            Assert.Multiple(() =>
            {
                Assert.That(smokeScript, Does.Contain("[string]$AppExecutable"));
                Assert.That(smokeScript, Does.Contain("[string]$DataDirectory"));
                Assert.That(smokeScript, Does.Contain("[string]$ExpectedAppVersion = \"\""));
                Assert.That(smokeScript, Does.Contain("-ArgumentList @(\"--export-diagnostics\", \"--data-dir\", $DataDirectory)"));
                Assert.That(smokeScript, Does.Contain("-RedirectStandardOutput $stdoutPath"));
                Assert.That(smokeScript, Does.Contain("-RedirectStandardError $stderrPath"));
                Assert.That(smokeScript, Does.Contain("Diagnostics bundle path was not reported."));
                Assert.That(smokeScript, Does.Contain("Diagnostics bundle was not created at $bundlePath."));
                Assert.That(smokeScript, Does.Contain("System.IO.Compression.ZipFile"));
                Assert.That(smokeScript, Does.Contain("diagnostics.json"));
                Assert.That(smokeScript, Does.Contain("ConvertFrom-Json"));
                Assert.That(smokeScript, Does.Contain("Diagnostics appVersion was"));
                Assert.That(smokeScript, Does.Contain("dataPaths"));
                Assert.That(smokeScript, Does.Contain("[data-directory]"));
                Assert.That(smokeScript, Does.Contain("[app-database]"));
                Assert.That(smokeScript, Does.Contain("[sync-state-database]"));
                Assert.That(smokeScript, Does.Contain("[token-store]"));
                Assert.That(smokeScript, Does.Contain("Public diagnostics JSON leaked private path value"));
                Assert.That(smokeScript, Does.Contain("sync-app.db"));
                Assert.That(smokeScript, Does.Contain("sync-state.db"));
                Assert.That(smokeScript, Does.Contain("tokens.json"));
                Assert.That(smokeScript, Does.Contain("Verified diagnostics bundle metadata:"));
                Assert.That(smokeScript, Does.Contain("Exported diagnostics bundle:"));
            });
        }

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
                Assert.That(script, Does.Contain("Get-FileHash -LiteralPath $appExecutable -Algorithm SHA256"));
                Assert.That(script, Does.Contain("CottonReleaseEvidenceWindowProbe"));
                Assert.That(script, Does.Contain("GetVisibleWindowsForProcess"));
                Assert.That(script, Does.Contain("GetForegroundProcessId"));
                Assert.That(script, Does.Contain("Capture-ProcessWindows"));
                Assert.That(script, Does.Contain("Capture-CloudFilesExplorerRegistrations"));
                Assert.That(script, Does.Contain("registry-cloud-files-explorer.txt"));
                Assert.That(script, Does.Contain("Capture-RootEntries"));
                Assert.That(script, Does.Contain("Capture-LogTails"));
                Assert.That(script, Does.Contain("Capture-VfsSmokeLogs"));
                Assert.That(script, Does.Contain("vfs-smoke"));
                Assert.That(script, Does.Contain("Redact-Text"));
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

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("[string]$EvidenceDirectory"));
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
                Assert.That(script, Does.Contain("vfs-smoke\\phase-leave-registered\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-reconnect-existing\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-initial-streaming-logging\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("vfs-smoke\\phase-steady-state-repeat\\cloud-files-vfs-smoke.stdout.log"));
                Assert.That(script, Does.Contain("Installed self-test: exitCode=0;"));
                Assert.That(script, Does.Contain("Diagnostics export: exitCode=0;"));
                Assert.That(script, Does.Contain("ObservedForeground: False"));
                Assert.That(script, Does.Contain("LaunchMode: attached-existing"));
                Assert.That(script, Does.Contain("CleanupRemaining: 0"));
                Assert.That(script, Does.Contain("RemainingRegistrationCount: 0"));
                Assert.That(script, Does.Contain("No Cloud Files or Explorer registration was captured before uninstall."));
                Assert.That(script, Does.Contain("VFS smoke logs: captured:"));
                Assert.That(script, Does.Contain("Desktop startup restored the saved signed-in session."));
                Assert.That(script, Does.Contain("Desktop startup reconnected the persisted Cloud Files sync root."));
                Assert.That(script, Does.Contain("Cloud Files sync root left registered for process restart smoke."));
                Assert.That(script, Does.Contain("Existing remote-only placeholder is available before reconnect hydration."));
                Assert.That(script, Does.Contain("Initial VFS trace log contains large-run metrics."));
                Assert.That(script, Does.Contain("Metric excerpt:"));
                Assert.That(script, Does.Contain("placeholders/sec"));
                Assert.That(script, Does.Contain("Steady-state repeat pass avoided local placeholder-tree scanning."));
                Assert.That(script, Does.Contain("fullLocalScans=0"));
                Assert.That(script, Does.Contain("metadataTreeScans=0"));
                Assert.That(script, Does.Contain("pathLookups=0"));
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
                Assert.That(script, Does.Contain("New-ScheduledTaskTrigger -AtLogOn"));
                Assert.That(script, Does.Contain("New-ScheduledTaskPrincipal"));
                Assert.That(script, Does.Contain("ConvertTo-CommandLineArgument"));
                Assert.That(script, Does.Contain("Assert-RequiredValue -Name \"OutputDirectory\""));
                Assert.That(script, Does.Contain("capture-vfs-release-evidence.ps1"));
                Assert.That(script, Does.Contain("-RunProfileSelfTest"));
                Assert.That(script, Does.Contain("-RunDiagnosticsExport"));
                Assert.That(script, Does.Contain("RunnerStartedAt:"));
                Assert.That(script, Does.Contain("TaskRegisteredAt:"));
                Assert.That(script, Does.Contain("LatestInteractiveLogonAt:"));
                Assert.That(script, Does.Contain("Win32_LogonSession"));
                Assert.That(script, Does.Contain("RunnerUser:"));
                Assert.That(script, Does.Contain("RunnerSessionId:"));
                Assert.That(script, Does.Contain("RunnerProcessId:"));
                Assert.That(script, Does.Contain("RunnerInteractive:"));
                Assert.That(script, Does.Contain("CaptureExitCode:"));
                Assert.That(script, Does.Contain("RunnerFinishedAt:"));
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

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingUpdateRelaunchEvidence()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.Delete(Path.Combine(evidenceDirectory, "update-relaunch.txt"));

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("update-relaunch.txt"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

        [Test]
        public void WindowsVfsReleaseEvidenceVerifierScript_RejectsMissingPostUninstallCleanupEvidence()
        {
            string evidenceDirectory = CreateVfsReleaseEvidenceBundle();
            try
            {
                File.Delete(Path.Combine(evidenceDirectory, "post-uninstall-cleanup.txt"));

                (int exitCode, string output) = RunVfsReleaseEvidenceVerifier(evidenceDirectory);

                Assert.That(exitCode, Is.Not.EqualTo(0), output);
                Assert.That(output, Does.Contain("post-uninstall-cleanup.txt"));
            }
            finally
            {
                DeleteTestDirectory(evidenceDirectory);
            }
        }

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
            Type nativeApiType = typeof(WindowsCloudFilesNativeApi);

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
                    Does.Contain("dotnet publish src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj /p:PublishProfile=win-x64"));
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
                Assert.That(installerScript, Does.Contain("[UninstallRun]"));
                Assert.That(installerScript, Does.Contain("Parameters: \"--cleanup-cloud-files\""));
                AssertCloudFilesImport(nativeApiType, "CfRegisterSyncRoot");
                AssertCloudFilesImport(nativeApiType, "CfUnregisterSyncRoot");
                AssertCloudFilesImport(nativeApiType, "CfCreatePlaceholders");
                AssertCloudFilesImport(nativeApiType, "CfConnectSyncRoot");
                AssertCloudFilesImport(nativeApiType, "CfDisconnectSyncRoot");
                AssertCloudFilesImport(nativeApiType, "CfSetPinState");
                AssertCloudFilesImport(nativeApiType, "CfConvertToPlaceholder");
                AssertCloudFilesImport(nativeApiType, "CfExecute");
                AssertCloudFilesImport(nativeApiType, "CfOpenFileWithOplock");
                AssertCloudFilesImport(nativeApiType, "CfDehydratePlaceholder");
                AssertCloudFilesImport(nativeApiType, "CfUpdatePlaceholder");
                AssertCloudFilesImport(nativeApiType, "CfCloseHandle");
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
                Assert.That(installerScript, Does.Contain("ValueData: \"\"\"{app}\\Cotton.Sync.Desktop.exe\"\" --start-minimized\""));
                Assert.That(installerScript, Does.Contain("Flags: uninsdeletevalue"));
                Assert.That(installerScript, Does.Contain(@"Software\Classes\*\shell\CottonSyncCopyShareLink"));
                Assert.That(installerScript, Does.Contain(@"Software\Classes\Directory\shell\CottonSyncCopyShareLink"));
                Assert.That(installerScript, Does.Contain("Copy Cotton Cloud share link"));
                Assert.That(installerScript, Does.Contain("--copy-shell-share-link"));
                Assert.That(installerScript, Does.Contain("Flags: nowait postinstall; Check: ShouldOfferLaunchAfterInstall"));
                Assert.That(installerScript, Does.Contain("Parameters: \"{code:GetHiddenUpdateLaunchParameters}\"; Flags: nowait; Check: ShouldLaunchHiddenAfterUpdate"));
                Assert.That(installerScript, Does.Contain("function ShouldOfferLaunchAfterInstall(): Boolean;"));
                Assert.That(installerScript, Does.Contain("function ShouldLaunchHiddenAfterUpdate(): Boolean;"));
                Assert.That(installerScript, Does.Contain("function GetHiddenUpdateLaunchParameters(Value: String): String;"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdate|0}') <> '1'"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdate|0}') = '1'"));
                Assert.That(installerScript, Does.Contain("ExpandConstant('{param:LaunchAfterUpdateDataDir|}')"));
                Assert.That(installerScript, Does.Contain("Result := '--start-minimized';"));
                Assert.That(installerScript, Does.Contain("Result := Result + ' --data-dir ' + AddQuotes(DataDirectory);"));
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
                Assert.That(workflow, Does.Contain("$vfsSmokeDataDir = Join-Path $env:RUNNER_TEMP \"cotton-sync-vfs-self-test-truthfulness-data\""));
                Assert.That(workflow, Does.Contain("$autostartReport = Join-Path $evidenceDir \"autostart-launch.txt\""));
                Assert.That(workflow, Does.Contain("New-Item -ItemType Directory -Path $evidenceDir -Force"));
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
                Assert.That(
                    workflow,
                    Does.Contain("-AdditionalVfsSmokePhases @(\"desktop-session-restore\", \"shell-share-link-targets\", \"initial-streaming-logging\", \"steady-state-repeat\")"));
                Assert.That(workflow, Does.Contain("-InitialStreamingPlaceholderCount 100000"));
                Assert.That(workflow, Does.Contain("-SteadyStateRepeatPlaceholderCount 100000"));
                Assert.That(workflow, Does.Contain("function Invoke-InstalledVfsSmokePhase"));
                Assert.That(workflow, Does.Contain("Invoke-InstalledVfsSmokePhase -PhaseName \"leave-registered\""));
                Assert.That(workflow, Does.Contain("Invoke-InstalledVfsSmokePhase -PhaseName \"reconnect-existing\""));
                Assert.That(workflow, Does.Contain("phase-reconnect-existing"));
                Assert.That(workflow, Does.Contain("--self-test --data-dir"));
                Assert.That(workflow, Does.Contain("-PublishDirectory $installDir"));
                Assert.That(workflow, Does.Contain("-AppExecutable $installedExe"));
                Assert.That(workflow, Does.Contain("-ExpectedIcon \"src/Cotton.Sync.Desktop/Assets/app.ico\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-diagnostics-export.ps1"));
                Assert.That(workflow, Does.Contain("-ExpectedAppVersion \"${{ steps.gitversion.outputs.SemVer }}\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-update-discovery.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-update-discovery-data"));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-update-visual-states.ps1"));
                Assert.That(workflow, Does.Contain("cotton-sync-update-visual-states-data"));
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
                Assert.That(workflow, Does.Contain("-LocalRoot \"S:\\CottonSyncVfsQa\\root\""));
                Assert.That(workflow, Does.Contain("-InstallDirectory $installDir"));
                Assert.That(workflow, Does.Contain("-VfsSmokeDataDirectory $vfsSmokeDataDir"));
                Assert.That(workflow, Does.Contain("-RunSelfTest"));
                Assert.That(workflow, Does.Contain("-RunDiagnosticsExport"));
                Assert.That(workflow, Does.Contain("-ExpectAbsent"));
                Assert.That(workflow, Does.Contain("unins000.exe"));
                Assert.That(workflow, Does.Contain("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
                Assert.That(workflow, Does.Contain("Autostart registry value was not installed correctly."));
                Assert.That(workflow, Does.Contain("$expectedRunValue = \"`\"$installedExe`\" --start-minimized\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/smoke-autostart-launch.ps1"));
                Assert.That(workflow, Does.Contain("-AppExecutable $installedExe"));
                Assert.That(workflow, Does.Contain("-ReportPath $autostartReport"));
                Assert.That(workflow, Does.Not.Contain("Set-ItemProperty -Path $runKey -Name \"Cotton Sync\""));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-cloud-files-cleanup.ps1"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-vfs-release-evidence.ps1"));
                Assert.That(workflow, Does.Contain("-EvidenceDirectory $evidenceDir"));
                Assert.That(
                    Regex.Matches(workflow, "Packaging/windows/verify-cloud-files-cleanup.ps1").Count,
                    Is.GreaterThanOrEqualTo(3));
                Assert.That(workflow, Does.Contain("Installed desktop executable remained after uninstall."));
                Assert.That(workflow, Does.Contain("Install directory was not empty after uninstall."));
                Assert.That(workflow, Does.Contain("Windows reinstall exited with code"));
                Assert.That(workflow, Does.Contain("Reinstalled desktop executable was not found."));
                Assert.That(workflow, Does.Contain("Windows reinstall cleanup exited with code"));
                Assert.That(workflow, Does.Contain("Install directory was not empty after reinstall cleanup."));
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
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
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
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
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
                    Does.Match("(?s)\\n  release:\\n    name: Publish Sync Client Release\\n    runs-on: ubuntu-latest\\n    needs:\\n      - linux\\n      - windows\\n      - cli-windows\\n      - release-checksums"));
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
                Assert.That(script, Does.Contain("Assert-ShellVerbVisibility"));
                Assert.That(script, Does.Contain("Assert-InstalledShellVerbInvocation"));
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
                Assert.That(script, Does.Contain("Preparing cloud files 118054 of 500000"));
                Assert.That(script, Does.Contain("118054 of 500000"));
                Assert.That(script, Does.Contain("118054 cloud items ready"));
                Assert.That(script, Does.Contain("[int]$StableObservationSeconds = 0"));
                Assert.That(script, Does.Contain("Assert-VisualStateSnapshot"));
                Assert.That(script, Does.Contain("-StableObservationSeconds 6"));
                Assert.That(script, Does.Contain("Observed visual state '$Scenario' sample(s):"));
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
                Assert.That(script, Does.Contain("lastInstallExitCode"));
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
                Assert.That(script, Does.Contain("$ReleaseTag = \"v$ExpectedAppVersion\""));
                Assert.That(script, Does.Contain("gh release download $ReleaseTag"));
                Assert.That(script, Does.Contain("--pattern CottonSync-Windows.zip"));
                Assert.That(script, Does.Contain("--pattern CottonSync-Windows-Setup.exe"));
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
                Assert.That(workflow, Does.Contain("& $installedExe --self-test --data-dir"));
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
                Assert.That(workflow, Does.Contain("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}"));
            });
        }

        [Test]
        public void CiWorkflow_DoesNotCarryMonorepoDockerImageWorkflow()
        {
            string desktopWorkflow = GetDesktopWorkflow();
            string? dockerWorkflowPath = TryGetRepositoryFilePath(Path.Combine(".github", "workflows", "docker-image.yml"));

            Assert.Multiple(() =>
            {
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.tar.gz"));
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-desktop-linux-x64-${{ steps.gitversion.outputs.SemVer }}.deb"));
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}.tar.gz"));
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-desktop-win-x64-${{ steps.gitversion.outputs.SemVer }}-setup.exe"));
                Assert.That(desktopWorkflow, Does.Contain("cotton-sync-cli-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(dockerWorkflowPath, Is.Null);
            });
        }

        [Test]
        public void DesktopWorkflow_UploadsReleaseArtifactChecksums()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("release-checksums:"));
                Assert.That(workflow, Does.Contain("Desktop Release Artifact Checksums"));
                Assert.That(workflow, Does.Contain("actions/download-artifact@v4"));
                Assert.That(workflow, Does.Contain("name: desktop-linux-x64"));
                Assert.That(workflow, Does.Contain("name: desktop-win-x64"));
                Assert.That(workflow, Does.Contain("name: desktop-windows-installer"));
                Assert.That(workflow, Does.Contain("name: sync-cli-windows-x64"));
                Assert.That(workflow, Does.Contain("release-artifact-checksums.sha256"));
                Assert.That(workflow, Does.Contain("name: release-artifact-checksums"));
                Assert.That(workflow, Does.Contain("Expected 6 desktop release assets"));
            });
        }

        [Test]
        public void DesktopWorkflow_PublishesSyncCliWindowsArtifact()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("cli-windows:"));
                Assert.That(workflow, Does.Contain("Sync CLI Windows Package Smoke"));
                Assert.That(workflow, Does.Contain("dotnet publish src/Cotton.Sync.Cli/Cotton.Sync.Cli.csproj"));
                Assert.That(workflow, Does.Contain("-p:AssemblyVersion='${{ steps.gitversion.outputs.AssemblyVersion }}'"));
                Assert.That(workflow, Does.Contain("-p:FileVersion='${{ steps.gitversion.outputs.FileVersion }}'"));
                Assert.That(workflow, Does.Contain("Cotton.Sync.Cli.exe"));
                Assert.That(workflow, Does.Contain("Packaging/windows/verify-version-metadata.ps1"));
                Assert.That(workflow, Does.Contain("-Label \"CLI publish executable\""));
                Assert.That(workflow, Does.Contain("-Label \"CLI zip executable\""));
                Assert.That(workflow, Does.Contain("auth-browser"));
                Assert.That(workflow, Does.Contain("state-summary"));
                Assert.That(workflow, Does.Contain("sync-once"));
                Assert.That(workflow, Does.Contain("sync-soak"));
                Assert.That(workflow, Does.Contain("cotton-sync-cli-win-x64-${{ steps.gitversion.outputs.SemVer }}.zip"));
                Assert.That(workflow, Does.Contain("CottonSync-CLI-Windows.zip"));
            });
        }

        [Test]
        public void DesktopWorkflow_PublishesGitHubReleaseAssets()
        {
            string workflow = GetDesktopWorkflow();

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("Publish Sync Client Release"));
                Assert.That(workflow, Does.Contain("contents: write"));
                Assert.That(workflow, Does.Contain("branches:"));
                Assert.That(workflow, Does.Contain("- main"));
                Assert.That(workflow, Does.Contain("- develop"));
                Assert.That(workflow, Does.Contain("tags:"));
                Assert.That(workflow, Does.Contain("- \"v*\""));
                Assert.That(workflow, Does.Contain("workflow_dispatch:"));
                Assert.That(workflow, Does.Contain("refs/heads/main"));
                Assert.That(workflow, Does.Not.Contain("    paths:"));
                Assert.That(workflow, Does.Contain("github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/tags/v') || (github.event_name == 'workflow_dispatch' && inputs.publish_release)"));
                Assert.That(workflow, Does.Contain("github.ref != 'refs/heads/main'"));
                Assert.That(workflow, Does.Contain("Pushes to main and v* tags produce and publish release assets automatically."));
                Assert.That(workflow, Does.Contain("fetch-depth: 0"));
                Assert.That(workflow, Does.Contain("Normalize desktop release asset names"));
                Assert.That(workflow, Does.Contain("release-assets/CottonSync-CLI-Windows.zip"));
                Assert.That(workflow, Does.Contain("release-assets/CottonSync-Windows-Setup.exe"));
                Assert.That(workflow, Does.Contain("release-assets/CottonSync-Windows.zip"));
                Assert.That(workflow, Does.Contain("release-assets/CottonSync-Linux.deb"));
                Assert.That(workflow, Does.Contain("release-assets/CottonSync-Linux.tar.gz"));
                Assert.That(workflow, Does.Contain("Delete stale release assets"));
                Assert.That(workflow, Does.Contain("gh release delete-asset \"$tag\" \"$asset_name\""));
                Assert.That(workflow, Does.Contain("allowed_names=$'CottonSync-CLI-Windows.zip"));
                Assert.That(workflow, Does.Contain("tag=\"v${version}\""));
                Assert.That(workflow, Does.Contain("prerelease=\"false\""));
                Assert.That(workflow, Does.Contain("RELEASE_TAG: v${{ needs.linux.outputs.Version }}"));
                Assert.That(workflow, Does.Contain("release-manifest.json"));
                Assert.That(workflow, Does.Contain("\"schemaVersion\": 1"));
                Assert.That(workflow, Does.Contain("\"product\": \"Cotton Sync\""));
                Assert.That(workflow, Does.Contain("\"releaseUrl\": release_url"));
                Assert.That(workflow, Does.Contain("release_download_url = f\"{server_url}/{repository}/releases/download/{tag}\""));
                Assert.That(workflow, Does.Contain("\"url\": f\"{release_download_url}/{path.name}\""));
                Assert.That(workflow, Does.Contain("ncipollo/release-action@v1"));
                Assert.That(workflow, Does.Contain("name: Cotton Sync Client ${{ needs.linux.outputs.Version }}"));
                Assert.That(workflow, Does.Contain("Generate release notes"));
                Assert.That(workflow, Does.Contain("git log --no-merges --pretty=format:'- %s (`%h`)'"));
                Assert.That(workflow, Does.Contain("git log --no-merges --max-count=50 --pretty=format:'- %s (`%h`)'"));
                Assert.That(workflow, Does.Contain("[Full changelog](${changelog_url})"));
                Assert.That(workflow, Does.Contain("bodyFile: release-notes.md"));
                Assert.That(workflow, Does.Not.Contain("Cotton Sync client release."));
                Assert.That(workflow, Does.Contain("artifacts: \"release-assets/*\""));
                Assert.That(workflow, Does.Contain("artifactErrorsFailBuild: true"));
                Assert.That(workflow, Does.Contain("allowUpdates: true"));
                Assert.That(workflow, Does.Contain("replacesArtifacts: true"));
                Assert.That(workflow, Does.Contain("makeLatest: true"));
                Assert.That(workflow, Does.Contain("prerelease: ${{ steps.release_metadata.outputs.prerelease }}"));
                Assert.That(workflow, Does.Contain("Expected 7 release files before manifest"));
            });
        }

        [Test]
        public void ReleaseVersioning_UsesLatestTagPlusOnePatchPolicy()
        {
            string gitVersion = File.ReadAllText(GetRepositoryFilePath("GitVersion.yml"));
            string versionScript = File.ReadAllText(GetRepositoryFilePath(Path.Combine(".github", "scripts", "determine-version.ps1")));
            string workflow = GetDesktopWorkflow();
            string toolManifest = File.ReadAllText(GetRepositoryFilePath("dotnet-tools.json"));

            Assert.Multiple(() =>
            {
                Assert.That(gitVersion, Does.Contain("next-version: 0.1.0"));
                Assert.That(gitVersion, Does.Not.Contain("next-version: 0.0.0"));
                Assert.That(gitVersion, Does.Contain("strategies:"));
                Assert.That(gitVersion, Does.Contain("- Mainline"));
                Assert.That(gitVersion, Does.Contain("increment: Patch"));
                Assert.That(versionScript, Does.Contain("Get-ReleasePolicyVersion"));
                Assert.That(versionScript, Does.Contain("git tag --points-at HEAD"));
                Assert.That(versionScript, Does.Contain("git tag --list \"v[0-9]*.[0-9]*.[0-9]*\""));
                Assert.That(versionScript, Does.Contain("Policy = \"latest-tag-plus-one\""));
                Assert.That(versionScript, Does.Contain("Policy = \"tag\""));
                Assert.That(versionScript, Does.Contain("VersionPolicy"));
                Assert.That(versionScript, Does.Contain("Release SemVer"));
                Assert.That(versionScript, Does.Contain("dotnet tool restore"));
                Assert.That(versionScript, Does.Contain("dotnet gitversion /output json"));
                Assert.That(versionScript, Does.Contain("$gitVersion.MajorMinorPatch"));
                Assert.That(versionScript, Does.Contain("GitVersionSemVer"));
                Assert.That(versionScript, Does.Contain("$assemblyVersion = \"$version.0\""));
                Assert.That(versionScript, Does.Contain("$fileVersion = \"$version.0\""));
                Assert.That(versionScript, Does.Contain("AssemblyVersion=$assemblyVersion"));
                Assert.That(versionScript, Does.Contain("FileVersion=$fileVersion"));
                Assert.That(workflow, Does.Not.Contain("    paths:"));
                Assert.That(workflow, Does.Contain("run: ./.github/scripts/determine-version.ps1"));
                Assert.That(workflow, Does.Contain("-p:AssemblyVersion='${{ steps.gitversion.outputs.AssemblyVersion }}'"));
                Assert.That(workflow, Does.Contain("-p:FileVersion='${{ steps.gitversion.outputs.FileVersion }}'"));
                Assert.That(toolManifest, Does.Contain("\"gitversion.tool\""));
                Assert.That(versionScript, Does.Not.Contain("GITHUB_RUN_NUMBER"));
                Assert.That(versionScript, Does.Not.Contain("version-run-number-offset"));
                Assert.That(versionScript, Does.Not.Contain("$version = $nextVersion"));
                Assert.That(versionScript, Does.Not.Contain("0.5.0"));
            });
        }

        private static string? GetProperty(XElement propertyGroup, string name)
        {
            return propertyGroup.Element(name)?.Value;
        }

        private static string CreateVfsReleaseEvidenceBundle()
        {
            string evidenceDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "vfs-release-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidenceDirectory);
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-desktop-session-restore"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-shell-share-link-targets"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-leave-registered"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-reconnect-existing"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-initial-streaming-logging"));
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "vfs-smoke", "phase-steady-state-repeat"));

            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "summary.txt"),
                new[]
                {
                    "Installed app: captured: installed-app.txt",
                    "Autostart registry: captured: registry-run.txt",
                    "Cotton process windows: captured: process-windows.txt",
                    "Cloud Files Explorer registrations: captured: registry-cloud-files-explorer.txt",
                    "Local root entries: captured: local-root-entries.csv",
                    "Log tails: captured 1 log file(s)",
                    "VFS smoke logs: captured: vfs-smoke; files=8",
                    "Installed self-test: exitCode=0; stdout=self-test.stdout.log; stderr=self-test.stderr.log",
                    "Diagnostics export: exitCode=0; stdout=diagnostics-export.stdout.log; stderr=diagnostics-export.stderr.log"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "installed-app.txt"),
                new[]
                {
                    "ProductVersion: 0.1.0",
                    "FileVersion: 0.1.0",
                    "Sha256: abc"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-run.txt"),
                "Cotton Sync --start-minimized");
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "autostart-launch.txt"),
                new[]
                {
                    "Result: passed",
                    "ExpectedRunValue: Cotton.Sync.Desktop.exe --start-minimized",
                    "CommandLine: Cotton.Sync.Desktop.exe --start-minimized",
                    "ObservedForeground: False",
                    "VisibleWindowCount: 0",
                    "CleanupRemaining: 0"
                });
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "update-relaunch.txt"),
                new[]
                {
                    "Result: passed",
                    "LaunchMode: attached-existing",
                    "ExpectedRunValue: Cotton.Sync.Desktop.exe --start-minimized",
                    "CommandLine: Cotton.Sync.Desktop.exe --start-minimized",
                    "ObservedForeground: False",
                    "VisibleWindowCount: 0",
                    "CleanupRemaining: 0"
                });
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-uninstall-cleanup.txt"));
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-reinstall-cleanup.txt"));
            WriteCleanupEvidence(Path.Combine(evidenceDirectory, "post-upgrade-cleanup.txt"));
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "process-windows.txt"),
                new[]
                {
                    "IsForeground : False",
                    "VisibleWindowCount : 0"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-cloud-files-explorer.txt"),
                "MatchCount: 1");
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "local-root-entries.csv"),
                string.Join(
                    Environment.NewLine,
                    "\"RelativePath\",\"FullPath\",\"Exists\",\"Attributes\",\"Length\",\"LastWriteTimeUtc\"",
                    "\".\",\"S:\\Cloud\",\"True\",\"Directory\",\"\",\"2026-06-24T10:00:00.0000000Z\""));
            File.WriteAllText(Path.Combine(evidenceDirectory, "self-test.stdout.log"), "Result: passed");
            File.WriteAllText(Path.Combine(evidenceDirectory, "diagnostics-export.stdout.log"), "Diagnostics exported");
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "vfs-smoke", "cloud-files-vfs-smoke.stdout.log"),
                "Result: passed");
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
                    "Result: passed"
                });
            File.WriteAllText(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-shell-share-link-targets",
                    "cloud-files-vfs-smoke.stdout.log"),
                "Result: passed");
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-leave-registered",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Cloud Files sync root left registered for process restart smoke.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-reconnect-existing",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Existing remote-only placeholder is available before reconnect hydration.",
                    "PASS: Cloud Files sync root unregistered after smoke.",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-initial-streaming-logging",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Initial VFS streaming run created a large placeholder baseline without per-placeholder activities.",
                    "PASS: Initial VFS trace log contains large-run metrics.",
                    "Metric excerpt: Completed initial streaming Windows virtual-files population for Cloud: 1 directories discovered, 100000 files discovered, remote pages read=100, remote page latency total=00:00:00.4000000, 100000 placeholders created or refreshed at 2500 placeholders/sec, state writes 100000 file rows in 196 file write batches, directory rows 1, managed heap start=1000000 peak=2000000 end=1500000, activities retained 0/0",
                    "Result: passed"
                });
            File.WriteAllLines(
                Path.Combine(
                    evidenceDirectory,
                    "vfs-smoke",
                    "phase-steady-state-repeat",
                    "cloud-files-vfs-smoke.stdout.log"),
                new[]
                {
                    "PASS: Steady-state repeat pass avoided local placeholder-tree scanning. files=100,000, syncElapsedMs=3000, streamingCrawls=1, fullLocalScans=0, metadataTreeScans=0, pathLookups=0, transfers=0, placeholderWrites=0",
                    "Result: passed"
                });

            return evidenceDirectory;
        }

        private static void WriteCleanupEvidence(string path)
        {
            File.WriteAllLines(
                path,
                new[]
                {
                    "Result: passed",
                    "RemainingRegistrationCount: 0"
                });
        }

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
                    "ProductVersion: 0.1.0",
                    "FileVersion: 0.1.0",
                    "Sha256: abc"
                });
            File.WriteAllText(
                Path.Combine(evidenceDirectory, "registry-run.txt"),
                "Cotton Sync --start-minimized");
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
                    "RunnerFinishedAt: 2026-06-24T10:02:00.0000000Z"
                });

            return evidenceDirectory;
        }

        private static (int ExitCode, string Output) RunVfsReleaseEvidenceVerifier(string evidenceDirectory)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(GetDesktopFilePath("Packaging/windows/verify-vfs-release-evidence.ps1"));
            startInfo.ArgumentList.Add("-EvidenceDirectory");
            startInfo.ArgumentList.Add(evidenceDirectory);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell verifier process did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("VFS release evidence verifier did not exit within 30 seconds.");
            }

            return (process.ExitCode, stdout + stderr);
        }

        private static (int ExitCode, string Output) RunVfsLogonEvidenceVerifier(string evidenceDirectory)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(GetDesktopFilePath("Packaging/windows/verify-vfs-logon-evidence.ps1"));
            startInfo.ArgumentList.Add("-EvidenceDirectory");
            startInfo.ArgumentList.Add(evidenceDirectory);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell verifier process did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("VFS logon evidence verifier did not exit within 30 seconds.");
            }

            return (process.ExitCode, stdout + stderr);
        }

        private static void DeleteTestDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string NormalizeProfilePath(string? value)
        {
            return (value ?? string.Empty).Replace('\\', '/');
        }

        private static void AssertCloudFilesImport(Type nativeApiType, string entryPoint)
        {
            MethodInfo? method = nativeApiType.GetMethod(entryPoint, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, entryPoint + " must stay in the desktop Cloud Files native bridge.");
            DllImportAttribute? dllImport = method!.GetCustomAttribute<DllImportAttribute>();
            Assert.That(dllImport, Is.Not.Null, entryPoint + " must use the Windows Cloud Files API.");
            Assert.That(dllImport!.Value, Is.EqualTo("CldApi.dll"));
            Assert.That(dllImport.ExactSpelling, Is.True);
        }

        private static string GetDesktopProjectPath()
        {
            return GetDesktopFilePath("Cotton.Sync.Desktop.csproj");
        }

        private static IEnumerable<string> VersionedApplicationProjectPaths()
        {
            yield return GetDesktopProjectPath();
            yield return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.Cli",
                "Cotton.Sync.Cli.csproj"));
        }

        private static string GetWindowsShellProjectPath()
        {
            return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.WindowsShell",
                "Cotton.Sync.WindowsShell.csproj"));
        }

        private static string GetWindowsShellProgramPath()
        {
            return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.WindowsShell",
                "Program.cs"));
        }

        private static string GetPublishProfilePath(string runtimeIdentifier)
        {
            return GetDesktopFilePath(Path.Combine("Properties", "PublishProfiles", runtimeIdentifier + ".pubxml"));
        }

        private static string GetDesktopFilePath(string relativePath)
        {
            string? path = TryGetRepositoryFilePath(Path.Combine("src", "Cotton.Sync.Desktop", relativePath));
            if (path is not null)
            {
                return path;
            }

            throw new FileNotFoundException(relativePath + " was not found from the test directory.");
        }

        private static string GetRepositoryFilePath(string relativePath)
        {
            string? path = TryGetRepositoryFilePath(relativePath);
            if (path is not null)
            {
                return path;
            }

            throw new FileNotFoundException(relativePath + " was not found from the test directory.");
        }

        private static string GetDesktopWorkflow()
        {
            return File.ReadAllText(GetRepositoryFilePath(Path.Combine(".github", "workflows", "desktop-sync.yml")));
        }

        private static string? TryGetRepositoryFilePath(string relativePath)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (parent == directory)
                {
                    break;
                }

                directory = parent ?? string.Empty;
            }

            return null;
        }
    }
}
