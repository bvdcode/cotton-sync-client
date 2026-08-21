// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesProviderMetadata
    {
        public const string ProviderId = "Cotton.Sync.Desktop";
        public const string ProviderName = "Cotton Cloud";
        public static readonly Guid ProviderGuid = Guid.Parse("6453b9dc-e042-4a73-a675-c5b2aa6c9607");

        public static string ResolveVersion()
        {
            string version = DesktopProductVersion.Current;
            return version.Length <= 255 ? version : version[..255];
        }
    }
}
