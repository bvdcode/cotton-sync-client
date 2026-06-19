// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Tests.Updates
{
    public class DesktopUpdateServiceTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-update-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void DefaultManifestUri_UsesLatestReleaseRedirect()
        {
            Assert.That(
                DesktopUpdateService.DefaultManifestUri.ToString(),
                Is.EqualTo("https://github.com/bvdcode/cotton-sync-client/releases/latest/download/release-manifest.json"));
        }

        [Test]
        public void ReleaseManifest_FromJson_ParsesReleaseMetadata()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer");
            DesktopReleaseManifest manifest = DesktopReleaseManifest.FromJson(CreateManifestJson(
                "0.0.2",
                installerBytes));

            Assert.Multiple(() =>
            {
                Assert.That(manifest.SchemaVersion, Is.EqualTo(1));
                Assert.That(manifest.Product, Is.EqualTo("Cotton Sync"));
                Assert.That(manifest.Version, Is.EqualTo("0.0.2"));
                Assert.That(manifest.Tag, Is.EqualTo("v0.0.2"));
                Assert.That(manifest.Assets, Has.Count.EqualTo(1));
                Assert.That(manifest.Assets[0].Name, Is.EqualTo("CottonSync-Windows-Setup.exe"));
                Assert.That(manifest.Assets[0].Sha256, Is.EqualTo(Sha256(installerBytes)));
            });
        }

        [Test]
        public async Task CheckAsync_ReportsAvailableInstallerForNewerWindowsRelease()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes)),
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64);

            DesktopUpdateCheckResult result = await service.CheckAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsUpdateAvailable, Is.True);
                Assert.That(result.CanDownloadInstaller, Is.True);
                Assert.That(result.CurrentVersion.ToString(), Is.EqualTo("0.0.1"));
                Assert.That(result.LatestVersion.ToString(), Is.EqualTo("0.0.2"));
                Assert.That(result.InstallerAsset?.Name, Is.EqualTo("CottonSync-Windows-Setup.exe"));
            });
        }

        [Test]
        public async Task CheckAsync_DoesNotReportUpdateForSameVersion()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v1");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.1", installerBytes)),
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64);

            DesktopUpdateCheckResult result = await service.CheckAsync();

            Assert.That(result.IsUpdateAvailable, Is.False);
        }

        [Test]
        public async Task CheckAsync_RetriesTransientManifestRequest()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            var handler = new SequenceHttpMessageHandler(
                _ => throw new HttpRequestException("firewall denied first request"),
                request => CreateBytesResponse(
                    request,
                    Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes))));
            using HttpClient httpClient = new(handler)
            {
                BaseAddress = new Uri("https://updates.local"),
            };
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64,
                retryBaseDelay: TimeSpan.Zero);

            DesktopUpdateCheckResult result = await service.CheckAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsUpdateAvailable, Is.True);
                Assert.That(handler.RequestCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task DownloadInstallerAsync_RetriesTransientInstallerResponse()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            var handler = new SequenceHttpMessageHandler(
                request => CreateBytesResponse(
                    request,
                    Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes))),
                request => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent("try later"),
                },
                request => CreateBytesResponse(request, installerBytes));
            using HttpClient httpClient = new(handler)
            {
                BaseAddress = new Uri("https://updates.local"),
            };
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64,
                retryBaseDelay: TimeSpan.Zero);

            DesktopUpdateCheckResult check = await service.CheckAsync();
            DesktopUpdateDownloadResult download = await service.DownloadInstallerAsync(check);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(download.FilePath), Is.EqualTo(installerBytes));
                Assert.That(handler.RequestCount, Is.EqualTo(3));
            });
        }

        [Test]
        public async Task CheckAsync_ReportsNoInstallerOnUnsupportedPlatform()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes)),
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.Unsupported);

            DesktopUpdateCheckResult result = await service.CheckAsync();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsUpdateAvailable, Is.True);
                Assert.That(result.CanDownloadInstaller, Is.False);
                Assert.That(result.InstallerAsset, Is.Null);
            });
        }

        [Test]
        public async Task DownloadInstallerAsync_WritesInstallerOnlyAfterSha256Verification()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes)),
                ["/CottonSync-Windows-Setup.exe"] = installerBytes,
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64);

            DesktopUpdateCheckResult check = await service.CheckAsync();
            var progressReports = new List<DesktopUpdateDownloadProgress>();
            DesktopUpdateDownloadResult download = await service.DownloadInstallerAsync(
                check,
                new InlineProgress<DesktopUpdateDownloadProgress>(progressReports.Add));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(download.FilePath), Is.True);
                Assert.That(download.FilePath, Does.EndWith(Path.Combine("0.0.2", "CottonSync-Windows-Setup.exe")));
                Assert.That(File.ReadAllBytes(download.FilePath), Is.EqualTo(installerBytes));
                Assert.That(download.Sha256, Is.EqualTo(Sha256(installerBytes)));
                Assert.That(download.SizeBytes, Is.EqualTo(installerBytes.Length));
                Assert.That(progressReports, Has.Count.GreaterThanOrEqualTo(2));
                Assert.That(progressReports[0].BytesDownloaded, Is.EqualTo(0));
                Assert.That(progressReports[0].TotalBytes, Is.EqualTo(installerBytes.Length));
                Assert.That(progressReports[^1].BytesDownloaded, Is.EqualTo(installerBytes.Length));
                Assert.That(progressReports[^1].TotalBytes, Is.EqualTo(installerBytes.Length));
            });
        }

        [Test]
        public async Task CheckAndDownload_WriteReleaseObservabilityTrace()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("installer-v2");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", installerBytes)),
                ["/CottonSync-Windows-Setup.exe"] = installerBytes,
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64);
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var listener = new TextWriterTraceListener(writer);
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

        [Test]
        public async Task DownloadInstallerAsync_DeletesTempFileWhenSha256DoesNotMatch()
        {
            byte[] manifestBytes = Encoding.UTF8.GetBytes("expected-installer");
            byte[] downloadedBytes = Encoding.UTF8.GetBytes("tampered-installer");
            using HttpClient httpClient = CreateHttpClient(new Dictionary<string, byte[]>
            {
                ["/manifest.json"] = Encoding.UTF8.GetBytes(CreateManifestJson("0.0.2", manifestBytes)),
                ["/CottonSync-Windows-Setup.exe"] = downloadedBytes,
            });
            var service = new DesktopUpdateService(
                httpClient,
                "0.0.1",
                _tempDirectory,
                new Uri("https://updates.local/manifest.json"),
                DesktopUpdatePlatform.WindowsX64);

            DesktopUpdateCheckResult check = await service.CheckAsync();

            Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(check));
            Assert.That(Directory.EnumerateFiles(_tempDirectory, "*", SearchOption.AllDirectories), Is.Empty);
        }

        private static HttpClient CreateHttpClient(Dictionary<string, byte[]> responses)
        {
            return new HttpClient(new StubHttpMessageHandler(responses))
            {
                BaseAddress = new Uri("https://updates.local"),
            };
        }

        private static string CreateManifestJson(string version, byte[] installerBytes)
        {
            return """
            {
              "schemaVersion": 1,
              "product": "Cotton Sync",
              "version": "__VERSION__",
              "tag": "v__VERSION__",
              "commit": "0123456789abcdef",
              "branch": "main",
              "releaseUrl": "https://github.com/bvdcode/cotton-sync-client/releases/tag/v__VERSION__",
              "assets": [
                {
                  "name": "CottonSync-Windows-Setup.exe",
                  "sha256": "__SHA256__",
                  "sizeBytes": __SIZE_BYTES__,
                  "url": "https://updates.local/CottonSync-Windows-Setup.exe"
                }
              ]
            }
            """
            .Replace("__VERSION__", version, StringComparison.Ordinal)
            .Replace("__SHA256__", Sha256(installerBytes), StringComparison.Ordinal)
            .Replace("__SIZE_BYTES__", installerBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static HttpResponseMessage CreateBytesResponse(HttpRequestMessage request, byte[] body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(body),
            };
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            public InlineProgress(Action<T> report)
            {
                _report = report;
            }

            public void Report(T value)
            {
                _report(value);
            }
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _responses;

            public StubHttpMessageHandler(Dictionary<string, byte[]> responses)
            {
                _responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                string path = request.RequestUri?.AbsolutePath
                    ?? throw new InvalidOperationException("Request URI is missing.");
                if (!_responses.TryGetValue(path, out byte[]? body))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(body),
                });
            }
        }

        private sealed class SequenceHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

            public SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
            }

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                if (!_responses.TryDequeue(out Func<HttpRequestMessage, HttpResponseMessage>? response))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                    });
                }

                return Task.FromResult(response(request));
            }
        }
    }
}
