// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal static class LiveSyncSmokeSeedPlan
    {
        public static IReadOnlyList<LiveSyncSmokeSeedFile> Build(int fileCount, DateTime createdAtUtc)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileCount);
            DateTime normalizedCreatedAtUtc = createdAtUtc.Kind == DateTimeKind.Utc
                ? createdAtUtc
                : createdAtUtc.ToUniversalTime();
            List<LiveSyncSmokeSeedFile> files = new(fileCount);
            for (int index = 0; index < fileCount; index++)
            {
                bool useFirstClient = index % 2 == 0;
                string clientDirectory = useFirstClient ? "client-a" : "client-b";
                string relativePath = "pre-existing/burst/"
                    + clientDirectory
                    + "/file-"
                    + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                    + ".bin";
                string content = index == 0
                    ? string.Empty
                    : "Cotton Sync Desktop live burst file "
                        + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine
                        + normalizedCreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine;
                files.Add(new LiveSyncSmokeSeedFile(useFirstClient, relativePath, content));
            }

            return files;
        }
    }

    internal record LiveSyncSmokeSeedFile(bool UseFirstClient, string RelativePath, string Content);
}
