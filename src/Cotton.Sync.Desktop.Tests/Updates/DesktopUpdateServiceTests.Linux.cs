// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public partial class DesktopUpdateServiceTests
    {
        [Test]
        public async Task CheckAndDownloadAsync_LinuxSelectsDebianPackageAndVerifiesContent()
        {
            byte[] packageBytes = Encoding.UTF8.GetBytes("linux-package-v2");
            string manifest = CreateManifestJson("0.0.2", packageBytes)
                .Replace("CottonSync-Windows-Setup.exe", "CottonSync-Linux.deb", StringComparison.Ordinal);
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(manifest),
                ["/CottonSync-Linux.deb"] = packageBytes,
            });
            DesktopUpdateService service = CreateLocalUpdateService(httpClient, platform: DesktopUpdatePlatform.LinuxDebX64);
            DesktopUpdateCheckResult check = await service.CheckAsync();
            DesktopUpdateDownloadResult download = await service.DownloadInstallerAsync(check);
            Assert.Multiple(() =>
            {
                Assert.That(check.InstallerAsset?.Name, Is.EqualTo("CottonSync-Linux.deb"));
                Assert.That(File.ReadAllBytes(download.FilePath), Is.EqualTo(packageBytes));
                Assert.That(Path.GetExtension(download.FilePath), Is.EqualTo(".deb"));
            });
        }

        [Test]
        public void FindInstallerAsset_RejectsInvalidPlatform()
        {
            DesktopReleaseManifest manifest = DesktopReleaseManifest.FromJson(
                CreateManifestJson("0.0.2", Encoding.UTF8.GetBytes("package")));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DesktopUpdateService.FindInstallerAsset(manifest, (DesktopUpdatePlatform)int.MaxValue));
        }
    }
}
