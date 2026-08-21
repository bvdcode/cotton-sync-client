// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop
{
    internal static class DesktopProductVersion
    {
        private const string AssemblyFileName = "Cotton.Sync.Desktop.dll";

        public static string Current => Resolve(Path.Combine(AppContext.BaseDirectory, AssemblyFileName));

        internal static string Resolve(string assemblyPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            if (!File.Exists(assemblyPath))
            {
                return "unknown";
            }

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(assemblyPath);
            string? version = string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                ? versionInfo.FileVersion
                : versionInfo.ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                return "unknown";
            }

            int metadataStart = version.IndexOf('+', StringComparison.Ordinal);
            return metadataStart > 0 ? version[..metadataStart] : version;
        }
    }
}
