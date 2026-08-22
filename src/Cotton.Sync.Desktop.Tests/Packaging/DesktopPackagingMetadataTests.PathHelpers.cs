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
        private static string GetDesktopProjectPath()
        {
            return GetDesktopFilePath("Cotton.Sync.Desktop.csproj");
        }

        private static IEnumerable<string> VersionedApplicationProjectPaths()
        {
            yield return GetDesktopProjectPath();
            yield return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.Cli",
                "Cotton.Sync.Cli.csproj"));
        }

        private static string GetWindowsShellProjectPath()
        {
            return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.WindowsShell",
                "Cotton.Sync.WindowsShell.csproj"));
        }

        private static string GetWindowsShellProgramPath()
        {
            return GetRepositoryFilePath(Path.Combine(
                "src",
                "Cotton.Sync.WindowsShell",
                "Program.cs"));
        }

        private static string GetPublishProfilePath(string runtimeIdentifier)
        {
            return GetDesktopFilePath(Path.Combine("Properties", "PublishProfiles", runtimeIdentifier + ".pubxml"));
        }

        private static string GetDesktopFilePath(string relativePath)
        {
            string? path = TryGetRepositoryFilePath(Path.Combine("src", "Cotton.Sync.Desktop", relativePath));
            if (path is not null)
            {
                return path;
            }

            throw new FileNotFoundException(relativePath + " was not found from the test directory.");
        }

        private static string GetRepositoryFilePath(string relativePath)
        {
            string? path = TryGetRepositoryFilePath(relativePath);
            if (path is not null)
            {
                return path;
            }

            throw new FileNotFoundException(relativePath + " was not found from the test directory.");
        }

        private static string GetDesktopWorkflow()
        {
            return File.ReadAllText(GetRepositoryFilePath(Path.Combine(".github", "workflows", "desktop-sync.yml")));
        }

        private static string? TryGetRepositoryFilePath(string relativePath)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (parent == directory)
                {
                    break;
                }

                directory = parent ?? string.Empty;
            }

            return null;
        }
    }
}
