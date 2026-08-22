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
            string workflow = GetDesktopWorkflow().Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(workflow, Does.Contain("tests:"));
                Assert.That(workflow, Does.Contain("Solution Tests"));
                Assert.That(workflow, Does.Contain("dotnet restore src/Cotton.sln"));
                Assert.That(workflow, Does.Contain("dotnet test src/Cotton.sln --no-restore -p:UseSharedCompilation=false"));
                Assert.That(workflow, Does.Contain("needs:\n      - tests\n    outputs:"));
                Assert.That(workflow, Does.Contain("needs:\n      - tests\n      - linux\n      - windows\n      - cli-windows"));
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
                Assert.That(workflow, Does.Contain("Validate release payload"));
                Assert.That(workflow, Does.Contain("required_assets=("));
                Assert.That(workflow, Does.Contain("Required release asset is missing or empty"));
                Assert.That(workflow, Does.Contain("Release notes body is missing or empty."));
                Assert.That(workflow, Does.Contain("Release manifest asset set mismatch."));
                Assert.That(workflow, Does.Contain("Release manifest asset has invalid size"));
                Assert.That(workflow, Does.Contain("Release manifest asset has no checksum"));
                Assert.That(workflow, Does.Contain("Release manifest asset has no download URL"));
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
    }
}
