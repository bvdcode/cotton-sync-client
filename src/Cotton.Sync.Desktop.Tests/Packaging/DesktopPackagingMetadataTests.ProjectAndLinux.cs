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

        [Test]
        public void DesktopWorkflow_SetsUpPinnedSdkBeforeDeterminingVersion()
        {
            string workflow = GetDesktopWorkflow();
            MatchCollection determineVersionSteps = Regex.Matches(
                workflow,
                @"(?m)^[ \t]+- name: Determine Version\r?$");

            Assert.That(determineVersionSteps, Has.Count.EqualTo(3));
            foreach (Match determineVersionStep in determineVersionSteps)
            {
                int checkoutIndex = workflow.LastIndexOf(
                    "- name: check repository",
                    determineVersionStep.Index,
                    StringComparison.Ordinal);
                int setupDotNetIndex = workflow.LastIndexOf(
                    "- name: Setup .NET",
                    determineVersionStep.Index,
                    StringComparison.Ordinal);

                Assert.That(
                    setupDotNetIndex,
                    Is.GreaterThan(checkoutIndex),
                    "Each package job must install the pinned SDK before running the GitVersion tool.");
            }
        }

        [Test]
        public void ReleaseBuildPolicy_PinsSdkLocksPackagesAndControlsDebugArtifacts()
        {
            string globalJson = File.ReadAllText(GetRepositoryFilePath("global.json"));
            XDocument buildProps = XDocument.Load(GetRepositoryFilePath("Directory.Build.props"));
            string workflow = GetDesktopWorkflow();
            XElement[] propertyGroups = buildProps.Root!.Elements("PropertyGroup").ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(globalJson, Does.Contain("\"version\": \"10.0.301\""));
                Assert.That(globalJson, Does.Contain("\"rollForward\": \"disable\""));
                Assert.That(
                    Regex.Matches(workflow, Regex.Escape("global-json-file: global.json")).Count,
                    Is.EqualTo(4));
                Assert.That(workflow, Does.Not.Contain("dotnet-version: 10.0.x"));
                Assert.That(
                    GetProperty(propertyGroups[0], "RestorePackagesWithLockFile"),
                    Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroups[0], "TreatWarningsAsErrors"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroups[0], "RestoreLockedMode"), Is.EqualTo("true"));
                Assert.That(GetProperty(propertyGroups[1], "DebugSymbols"), Is.EqualTo("false"));
                Assert.That(GetProperty(propertyGroups[1], "DebugType"), Is.EqualTo("none"));
                Assert.That(workflow, Does.Contain("dotnet restore src/Cotton.sln --locked-mode"));
                Assert.That(
                    Regex.Matches(
                        workflow,
                        Regex.Escape("dotnet restore src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj --locked-mode")).Count,
                    Is.EqualTo(2));
                Assert.That(workflow, Does.Contain("dotnet restore src/Cotton.Sync.Cli/Cotton.Sync.Cli.csproj --locked-mode"));
                Assert.That(workflow, Does.Contain("dotnet publish src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj --no-restore /p:PublishProfile=linux-x64"));
                Assert.That(workflow, Does.Contain("dotnet publish src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj --no-restore /p:PublishProfile=win-x64"));
                Assert.That(workflow, Does.Contain("dotnet publish src/Cotton.Sync.Cli/Cotton.Sync.Cli.csproj --no-restore -c Release -r win-x64"));
                Assert.That(File.Exists(GetRepositoryFilePath(Path.Combine("src", "Cotton.Sync.Desktop", "packages.lock.json"))), Is.True);
                Assert.That(File.Exists(GetRepositoryFilePath(Path.Combine("src", "Cotton.Sync.Cli", "packages.lock.json"))), Is.True);
                Assert.That(File.Exists(GetRepositoryFilePath(Path.Combine("src", "Cotton.Sync", "packages.lock.json"))), Is.True);
                Assert.That(File.Exists(GetRepositoryFilePath(Path.Combine("src", "Cotton.Sync.App", "packages.lock.json"))), Is.True);
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
                Assert.That(smokeScript, Does.Contain("set -- connecting sign-in-error empty-dashboard add-folder dashboard folder-controls progress many-small-download settings settings-diagnostics error conflict"));
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
    }
}
