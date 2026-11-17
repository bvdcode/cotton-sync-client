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
                relativePath = index switch
                {
                    2 => "pre-existing/Unicode/Проверка синхронизации.txt",
                    3 => "pre-existing/Names with spaces/report.final.v2.json",
                    4 => "pre-existing/Nested/one/two/three/four/data.csv",
                    5 => "pre-existing/Unicode/café-данные.txt",
                    6 => "pre-existing/Unicode/Файл 📁.txt",
                    7 => "pre-existing/large/content.bin",
                    _ => relativePath,
                };
                string content = index == 0
                    ? string.Empty
                    : "Cotton Sync Desktop live burst file "
                        + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine
                        + normalizedCreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine;
                if (index == 7)
                {
                    const int largeFileSize = 8 * 1024 * 1024;
                    content += new string('x', largeFileSize);
                }

                files.Add(new LiveSyncSmokeSeedFile(useFirstClient, relativePath, content));
            }

            return files;
        }
    }
}
