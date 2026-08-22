// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Text;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public partial class DesktopUpdateServiceTests
    {
        [Test]
        public async Task CheckAndDownload_WriteReleaseObservabilityTrace()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes)),
                ["/CottonSync-Windows-Setup.exe"] = installerBytes,
            });
            DesktopUpdateService service = CreateLocalUpdateService(httpClient);
            using StringWriter writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            TextWriterTraceListener listener = new TextWriterTraceListener(writer);
            Trace.Listeners.Add(listener);
            try
            {
                DesktopUpdateCheckResult check = await service.CheckAsync();
                _ = await service.DownloadInstallerAsync(check);
                listener.Flush();
                Trace.Flush();
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }

            string trace = writer.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(trace, Does.Contain("manifestUri=https://updates.local/manifest.json"));
                Assert.That(trace, Does.Contain("currentVersion=0.0.1"));
                Assert.That(trace, Does.Contain("latestVersion=0.0.2"));
                Assert.That(trace, Does.Contain("updateAvailable=True"));
                Assert.That(trace, Does.Contain("installerAsset=CottonSync-Windows-Setup.exe"));
                Assert.That(trace, Does.Contain("updateCacheDirectory=" + _tempDirectory));
                Assert.That(trace, Does.Contain("sizeBytes=" + installerBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });
        }
    }
}
