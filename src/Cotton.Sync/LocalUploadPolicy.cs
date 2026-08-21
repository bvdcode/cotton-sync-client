// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync
{
    internal static class LocalUploadPolicy
    {
        public static bool ShouldDefer(
            LocalFileSnapshot local,
            SyncRunOptions options,
            out TimeSpan remainingQuietTime)
        {
            remainingQuietTime = TimeSpan.Zero;
            if (options.MinimumLocalUploadAge <= TimeSpan.Zero)
            {
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan age = nowUtc - local.LastWriteUtc.ToUniversalTime();
            if (age >= options.MinimumLocalUploadAge)
            {
                return false;
            }

            remainingQuietTime = options.MinimumLocalUploadAge - age;
            return true;
        }

        public static void ReportDeferred(
            SyncRunResult result,
            SyncRunOptions options,
            string relativePath,
            TimeSpan remainingQuietTime)
        {
            result.RecordDeferredLocalPath(relativePath);
            string details = "Local file is still changing; retry after "
                + FormatQuietTime(remainingQuietTime)
                + " quiet window.";
            SyncActivityReporter.Record(result, options, SyncActivityKind.Skipped, relativePath, details);
        }

        private static string FormatQuietTime(TimeSpan value)
        {
            if (value.TotalMilliseconds < 1000)
            {
                return Math.Ceiling(value.TotalMilliseconds)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "ms";
            }

            return value.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }
    }
}
