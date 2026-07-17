// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class LiveSyncSmokeStateExpectation
    {
        public static IReadOnlyList<string> BuildRelativePaths(IEnumerable<string> relativeFilePaths)
        {
            ArgumentNullException.ThrowIfNull(relativeFilePaths);
            HashSet<string> expectedPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (string relativeFilePath in relativeFilePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativeFilePath);
                expectedPaths.Add(normalizedPath);

                int separatorIndex = normalizedPath.LastIndexOf('/');
                while (separatorIndex > 0)
                {
                    string directoryPath = normalizedPath[..separatorIndex];
                    expectedPaths.Add(directoryPath);
                    separatorIndex = directoryPath.LastIndexOf('/');
                }
            }

            return expectedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
