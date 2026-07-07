// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal class DesktopUpdateSourceTrustPolicy
    {
        private const string DefaultRepositoryPathPrefix = "/bvdcode/cotton-sync-client/";
        private const string ManifestKind = "manifest";
        private const string ReleaseKind = "release";
        private const string AssetKind = "asset";

        private readonly HashSet<string> _allowedHosts;
        private readonly bool _allowInsecureLoopback;
        private readonly bool _requireGitHubReleasePath;

        private DesktopUpdateSourceTrustPolicy(
            IEnumerable<string> allowedHosts,
            bool allowInsecureLoopback,
            bool requireGitHubReleasePath)
        {
            _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
            _allowInsecureLoopback = allowInsecureLoopback;
            _requireGitHubReleasePath = requireGitHubReleasePath;
        }

        public static DesktopUpdateSourceTrustPolicy CreateDefault()
        {
            return new DesktopUpdateSourceTrustPolicy(["github.com"], false, true);
        }

        internal static DesktopUpdateSourceTrustPolicy CreateForSmokeManifest(Uri manifestUri)
        {
            ArgumentNullException.ThrowIfNull(manifestUri);
            return new DesktopUpdateSourceTrustPolicy([manifestUri.Host], true, false);
        }

        internal static DesktopUpdateSourceTrustPolicy CreateForHost(Uri manifestUri)
        {
            ArgumentNullException.ThrowIfNull(manifestUri);
            return new DesktopUpdateSourceTrustPolicy([manifestUri.Host], false, false);
        }

        public void ValidateManifestUri(Uri manifestUri)
        {
            ValidateUri(manifestUri, ManifestKind);
        }

        public void ValidateManifest(DesktopReleaseManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ValidateUri(manifest.ReleaseUrl, ReleaseKind);
            foreach (DesktopReleaseAsset asset in manifest.Assets)
            {
                ValidateAsset(asset);
            }
        }

        public void ValidateAsset(DesktopReleaseAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);
            ValidateUri(asset.Url, AssetKind);
        }

        private void ValidateUri(Uri uri, string sourceKind)
        {
            if (!uri.IsAbsoluteUri)
            {
                throw new InvalidDataException("Desktop update " + sourceKind + " URL must be absolute.");
            }

            if (IsAllowedInsecureLoopbackUri(uri))
            {
                return;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Desktop update " + sourceKind + " URL must use HTTPS.");
            }

            if (!_allowedHosts.Contains(uri.Host))
            {
                throw new InvalidDataException("Desktop update " + sourceKind + " URL uses an unexpected host.");
            }

            if (!uri.IsDefaultPort)
            {
                throw new InvalidDataException("Desktop update " + sourceKind + " URL uses an unexpected port.");
            }

            if (_requireGitHubReleasePath && !IsExpectedGitHubReleasePath(uri.AbsolutePath, sourceKind))
            {
                throw new InvalidDataException("Desktop update " + sourceKind + " URL uses an unexpected release path.");
            }
        }

        private bool IsAllowedInsecureLoopbackUri(Uri uri)
        {
            return _allowInsecureLoopback
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && uri.IsLoopback;
        }

        private static bool IsExpectedGitHubReleasePath(string absolutePath, string sourceKind)
        {
            if (!absolutePath.StartsWith(DefaultRepositoryPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string repositoryRelativePath = absolutePath[DefaultRepositoryPathPrefix.Length..];
            return sourceKind switch
            {
                ManifestKind => string.Equals(
                    repositoryRelativePath,
                    "releases/latest/download/release-manifest.json",
                    StringComparison.OrdinalIgnoreCase)
                    || IsReleaseDownloadPath(repositoryRelativePath, "release-manifest.json"),
                ReleaseKind => repositoryRelativePath.StartsWith(
                    "releases/tag/",
                    StringComparison.OrdinalIgnoreCase),
                AssetKind => IsReleaseDownloadPath(repositoryRelativePath, null),
                _ => false,
            };
        }

        private static bool IsReleaseDownloadPath(string repositoryRelativePath, string? expectedFileName)
        {
            const string releaseDownloadPrefix = "releases/download/";
            if (!repositoryRelativePath.StartsWith(releaseDownloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string downloadRelativePath = repositoryRelativePath[releaseDownloadPrefix.Length..];
            string[] segments = downloadRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
            {
                return false;
            }

            return expectedFileName is null
                || string.Equals(segments[1], expectedFileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
