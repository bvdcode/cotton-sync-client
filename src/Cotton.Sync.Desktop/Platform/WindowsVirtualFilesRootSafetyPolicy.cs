// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsVirtualFilesRootSafetyPolicy
    {
        private readonly Func<string> _getCurrentDirectory;
        private readonly Func<Environment.SpecialFolder, string> _getSpecialFolderPath;

        public WindowsVirtualFilesRootSafetyPolicy(
            Func<Environment.SpecialFolder, string>? getSpecialFolderPath = null,
            Func<string>? getCurrentDirectory = null)
        {
            _getSpecialFolderPath = getSpecialFolderPath ?? Environment.GetFolderPath;
            _getCurrentDirectory = getCurrentDirectory ?? Directory.GetCurrentDirectory;
        }

        public WindowsVirtualFilesRootSafetyResult Validate(string localRootPath)
        {
            if (string.IsNullOrWhiteSpace(localRootPath))
            {
                return WindowsVirtualFilesRootSafetyResult.Unsafe(
                    WindowsVirtualFilesRootSafetyIssue.EmptyPath,
                    string.Empty,
                    "Virtual-files sync root is required.");
            }

            string trimmed = localRootPath.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return WindowsVirtualFilesRootSafetyResult.Unsafe(
                    WindowsVirtualFilesRootSafetyIssue.RelativePath,
                    trimmed,
                    "Virtual-files sync root must be an absolute Windows path.");
            }

            string fullPath;
            try
            {
                fullPath = NormalizeFullPath(trimmed);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return WindowsVirtualFilesRootSafetyResult.Unsafe(
                    WindowsVirtualFilesRootSafetyIssue.InvalidPath,
                    trimmed,
                    "Virtual-files sync root path is invalid.");
            }

            string root = NormalizeFullPath(Path.GetPathRoot(fullPath) ?? string.Empty);
            if (IsSamePath(fullPath, root))
            {
                return WindowsVirtualFilesRootSafetyResult.Unsafe(
                    WindowsVirtualFilesRootSafetyIssue.DriveRoot,
                    fullPath,
                    "Virtual-files sync root cannot be a drive or share root.");
            }

            WindowsVirtualFilesRootSafetyResult? protectedRootFailure = ValidateProtectedRoots(fullPath);
            if (protectedRootFailure is not null)
            {
                return protectedRootFailure;
            }

            string? repositoryRoot = FindRepositoryRoot();
            if (!string.IsNullOrWhiteSpace(repositoryRoot)
                && IsSameOrUnder(fullPath, repositoryRoot))
            {
                return WindowsVirtualFilesRootSafetyResult.Unsafe(
                    WindowsVirtualFilesRootSafetyIssue.RepositoryRoot,
                    fullPath,
                    "Virtual-files sync root cannot be inside the source repository.");
            }

            return WindowsVirtualFilesRootSafetyResult.Safe(fullPath);
        }

        private WindowsVirtualFilesRootSafetyResult? ValidateProtectedRoots(string fullPath)
        {
            foreach ((Environment.SpecialFolder folder, WindowsVirtualFilesRootSafetyIssue issue, string details) in ProtectedRoots())
            {
                string specialFolderPath = _getSpecialFolderPath(folder);
                if (string.IsNullOrWhiteSpace(specialFolderPath))
                {
                    continue;
                }

                string protectedRoot = NormalizeFullPath(specialFolderPath);
                if (IsSamePath(fullPath, protectedRoot))
                {
                    return WindowsVirtualFilesRootSafetyResult.Unsafe(issue, fullPath, details);
                }
            }

            return null;
        }

        private static IEnumerable<(Environment.SpecialFolder Folder, WindowsVirtualFilesRootSafetyIssue Issue, string Details)> ProtectedRoots()
        {
            yield return (
                Environment.SpecialFolder.UserProfile,
                WindowsVirtualFilesRootSafetyIssue.UserProfileRoot,
                "Virtual-files sync root cannot be the whole user profile.");
            yield return (
                Environment.SpecialFolder.Windows,
                WindowsVirtualFilesRootSafetyIssue.WindowsRoot,
                "Virtual-files sync root cannot be the Windows directory.");
            yield return (
                Environment.SpecialFolder.ProgramFiles,
                WindowsVirtualFilesRootSafetyIssue.ProgramFilesRoot,
                "Virtual-files sync root cannot be Program Files.");
            yield return (
                Environment.SpecialFolder.ProgramFilesX86,
                WindowsVirtualFilesRootSafetyIssue.ProgramFilesRoot,
                "Virtual-files sync root cannot be Program Files.");
        }

        private string? FindRepositoryRoot()
        {
            string currentDirectory;
            try
            {
                currentDirectory = NormalizeFullPath(_getCurrentDirectory());
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }

            var directory = new DirectoryInfo(currentDirectory);
            while (directory is not null)
            {
                string gitDirectory = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
                {
                    return NormalizeFullPath(directory.FullName);
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static bool IsSameOrUnder(string candidate, string root)
        {
            return IsSamePath(candidate, root)
                || candidate.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSamePath(string left, string right)
        {
            return string.Equals(
                NormalizeFullPath(left),
                NormalizeFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            while (fullPath.Length > (root?.Length ?? 0) && Path.EndsInDirectorySeparator(fullPath))
            {
                fullPath = fullPath[..^1];
            }

            return fullPath;
        }
    }
}
