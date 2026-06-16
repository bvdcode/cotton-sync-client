// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Text.Json;

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed record DesktopReleaseManifest(
        int SchemaVersion,
        string Product,
        string Version,
        string Tag,
        string Commit,
        string Branch,
        Uri ReleaseUrl,
        IReadOnlyList<DesktopReleaseAsset> Assets)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public DesktopSemanticVersion ParsedVersion => DesktopSemanticVersion.Parse(Version);

        public static DesktopReleaseManifest FromJson(string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            ManifestDto dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
                ?? throw new InvalidDataException("Release manifest is empty.");

            if (dto.SchemaVersion != 1)
            {
                throw new InvalidDataException("Unsupported release manifest schema version.");
            }

            string product = RequireText(dto.Product, "product");
            string version = RequireText(dto.Version, "version");
            _ = DesktopSemanticVersion.Parse(version);
            string tag = RequireText(dto.Tag, "tag");
            string commit = RequireText(dto.Commit, "commit");
            string branch = RequireText(dto.Branch, "branch");
            Uri releaseUrl = RequireAbsoluteUri(dto.ReleaseUrl, "releaseUrl");
            List<DesktopReleaseAsset> assets = (dto.Assets ?? [])
                .Select(ToAsset)
                .ToList();
            if (assets.Count == 0)
            {
                throw new InvalidDataException("Release manifest does not include assets.");
            }

            return new DesktopReleaseManifest(
                dto.SchemaVersion,
                product,
                version,
                tag,
                commit,
                branch,
                releaseUrl,
                assets);
        }

        private static DesktopReleaseAsset ToAsset(AssetDto dto)
        {
            string name = RequireText(dto.Name, "asset.name");
            string sha256 = RequireSha256(dto.Sha256);
            if (dto.SizeBytes < 0)
            {
                throw new InvalidDataException("Release asset size must not be negative.");
            }

            Uri url = RequireAbsoluteUri(dto.Url, "asset.url");
            return new DesktopReleaseAsset(name, sha256, dto.SizeBytes, url);
        }

        private static string RequireText(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("Release manifest field is required: " + name);
            }

            return value.Trim();
        }

        private static Uri RequireAbsoluteUri(string? value, string name)
        {
            string text = RequireText(value, name);
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            {
                throw new InvalidDataException("Release manifest field must be an absolute URL: " + name);
            }

            return uri;
        }

        private static string RequireSha256(string? value)
        {
            string text = RequireText(value, "asset.sha256");
            if (text.Length != 64 || text.Any(static character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("Release asset SHA-256 must be a 64-character hex string.");
            }

            return text.ToLowerInvariant();
        }

        private sealed class ManifestDto
        {
            public int SchemaVersion { get; set; }

            public string? Product { get; set; }

            public string? Version { get; set; }

            public string? Tag { get; set; }

            public string? Commit { get; set; }

            public string? Branch { get; set; }

            public string? ReleaseUrl { get; set; }

            public List<AssetDto>? Assets { get; set; }
        }

        private sealed class AssetDto
        {
            public string? Name { get; set; }

            public string? Sha256 { get; set; }

            public long SizeBytes { get; set; }

            public string? Url { get; set; }
        }
    }
}
