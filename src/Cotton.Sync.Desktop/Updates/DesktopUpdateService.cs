// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed class DesktopUpdateService : IDesktopUpdateService, IDisposable
    {
        public static readonly Uri DefaultManifestUri = new(
            "https://github.com/bvdcode/cotton-sync-client/releases/latest/download/release-manifest.json");

        private const string WindowsInstallerAssetName = "CottonSync-Windows-Setup.exe";

        private readonly HttpClient _httpClient;
        private readonly string _currentVersion;
        private readonly string _updateCacheDirectory;
        private readonly Uri _manifestUri;
        private readonly DesktopUpdatePlatform _platform;
        private readonly int _maxAttempts;
        private readonly TimeSpan _retryBaseDelay;
        private readonly bool _disposeHttpClient;

        public DesktopUpdateService(
            HttpClient httpClient,
            string currentVersion,
            string updateCacheDirectory,
            Uri? manifestUri = null,
            DesktopUpdatePlatform? platform = null,
            int maxAttempts = 3,
            TimeSpan? retryBaseDelay = null,
            bool disposeHttpClient = false)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _currentVersion = string.IsNullOrWhiteSpace(currentVersion)
                ? throw new ArgumentException("Current version is required.", nameof(currentVersion))
                : currentVersion.Trim();
            _updateCacheDirectory = string.IsNullOrWhiteSpace(updateCacheDirectory)
                ? throw new ArgumentException("Update cache directory is required.", nameof(updateCacheDirectory))
                : updateCacheDirectory;
            _manifestUri = manifestUri ?? DefaultManifestUri;
            if (!_manifestUri.IsAbsoluteUri)
            {
                throw new ArgumentException("Manifest URI must be absolute.", nameof(manifestUri));
            }

            _platform = platform ?? GetCurrentPlatform();
            ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
            _maxAttempts = maxAttempts;
            _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
            ArgumentOutOfRangeException.ThrowIfLessThan(_retryBaseDelay, TimeSpan.Zero);
            _disposeHttpClient = disposeHttpClient;
        }

        public void Dispose()
        {
            if (_disposeHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        public async Task<DesktopUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await SendWithRetryAsync(
                    token => _httpClient.GetAsync(_manifestUri, token),
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            DesktopReleaseManifest manifest = DesktopReleaseManifest.FromJson(json);
            DesktopSemanticVersion currentVersion = DesktopSemanticVersion.Parse(_currentVersion);
            DesktopSemanticVersion latestVersion = manifest.ParsedVersion;
            bool updateAvailable = latestVersion.CompareTo(currentVersion) > 0;
            DesktopReleaseAsset? installerAsset = FindInstallerAsset(manifest, _platform);
            return new DesktopUpdateCheckResult(
                manifest,
                currentVersion,
                latestVersion,
                updateAvailable,
                installerAsset);
        }

        public async Task<DesktopUpdateDownloadResult> DownloadInstallerAsync(
            DesktopUpdateCheckResult checkResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checkResult);
            if (!checkResult.IsUpdateAvailable)
            {
                throw new InvalidOperationException("No newer Cotton Sync release is available.");
            }

            DesktopReleaseAsset asset = checkResult.InstallerAsset
                ?? throw new InvalidOperationException("The latest Cotton Sync release does not include a Windows installer.");
            string versionDirectory = Path.Combine(_updateCacheDirectory, SanitizePathSegment(checkResult.LatestVersion.ToString()));
            Directory.CreateDirectory(versionDirectory);
            string finalPath = Path.Combine(versionDirectory, asset.Name);
            if (File.Exists(finalPath))
            {
                FileHashSnapshot existing = await HashFileAsync(finalPath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existing.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                    && existing.SizeBytes == asset.SizeBytes)
                {
                    return new DesktopUpdateDownloadResult(
                        checkResult.Manifest,
                        asset,
                        finalPath,
                        existing.Sha256,
                        existing.SizeBytes);
                }

                File.Delete(finalPath);
            }

            string tempPath = finalPath + ".download";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            try
            {
                using HttpResponseMessage response = await SendWithRetryAsync(
                        token => _httpClient.GetAsync(
                            asset.Url,
                            HttpCompletionOption.ResponseHeadersRead,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream local = new(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 128,
                    useAsync: true))
                {
                    await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                }

                FileHashSnapshot hash = await HashFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (hash.SizeBytes != asset.SizeBytes)
                {
                    throw new InvalidDataException(
                        "Downloaded update size "
                        + hash.SizeBytes.ToString(CultureInfo.InvariantCulture)
                        + " does not match manifest size "
                        + asset.SizeBytes.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }

                if (!string.Equals(hash.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Downloaded update SHA-256 does not match release manifest.");
                }

                File.Move(tempPath, finalPath);
                return new DesktopUpdateDownloadResult(
                    checkResult.Manifest,
                    asset,
                    finalPath,
                    hash.Sha256,
                    hash.SizeBytes);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                throw;
            }
        }

        internal static DesktopReleaseAsset? FindInstallerAsset(
            DesktopReleaseManifest manifest,
            DesktopUpdatePlatform platform)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            return platform switch
            {
                DesktopUpdatePlatform.WindowsX64 => manifest.Assets.FirstOrDefault(static asset =>
                    string.Equals(asset.Name, WindowsInstallerAssetName, StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    HttpResponseMessage response = await sendAsync(cancellationToken).ConfigureAwait(false);
                    if (attempt == _maxAttempts || !ShouldRetry(response.StatusCode))
                    {
                        return response;
                    }

                    response.Dispose();
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < _maxAttempts)
                {
                    lastException = exception;
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }

            throw lastException ?? new HttpRequestException("HTTP request failed before receiving a response.");
        }

        private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
        {
            if (_retryBaseDelay == TimeSpan.Zero)
            {
                return;
            }

            double multiplier = Math.Pow(2, attempt - 1);
            TimeSpan delay = _retryBaseDelay * multiplier;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return statusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 || code >= 500;
        }

        private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        {
            return !cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException;
        }

        private static DesktopUpdatePlatform GetCurrentPlatform()
        {
            return OperatingSystem.IsWindows()
                ? DesktopUpdatePlatform.WindowsX64
                : DesktopUpdatePlatform.Unsupported;
        }

        private static string SanitizePathSegment(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
            }

            return builder.ToString();
        }

        private static async Task<FileHashSnapshot> HashFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 128,
                useAsync: true);
            using var sha256 = SHA256.Create();
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return new FileHashSnapshot(Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
        }

        private sealed record FileHashSnapshot(string Sha256, long SizeBytes);
    }
}
