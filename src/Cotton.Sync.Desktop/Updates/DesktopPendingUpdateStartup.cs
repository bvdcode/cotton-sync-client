// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using Cotton.Sync.Desktop.Composition;

namespace Cotton.Sync.Desktop.Updates
{
    internal static class DesktopPendingUpdateStartup
    {
        private const int MaxStartupAttempts = 3;

        public static bool TryStartPendingUpdate(
            DesktopAppPaths paths,
            string currentVersion,
            IDesktopUpdateInstaller? installer = null)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
            var store = new DesktopPendingUpdateStore(paths.UpdateCacheDirectory);
            DesktopPendingUpdate? update = store.TryLoad();
            if (update is null)
            {
                return false;
            }

            try
            {
                if (!IsNewerThanCurrent(update.Version, currentVersion))
                {
                    store.Delete();
                    return false;
                }

                if (update.AttemptCount >= MaxStartupAttempts)
                {
                    store.Delete();
                    Trace.TraceWarning("Skipping pending Cotton Sync update after {0} startup attempts.", update.AttemptCount);
                    return false;
                }

                if (!IsInstallerValid(update))
                {
                    store.Delete();
                    return false;
                }

                store.Save(update with { AttemptCount = update.AttemptCount + 1 });
                (installer ?? new DesktopUpdateInstaller()).StartSilentInstall(
                    update.InstallerPath,
                    launchAfterUpdate: true);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Failed to start pending Cotton Sync update: {0}", exception);
                return false;
            }
        }

        private static bool IsNewerThanCurrent(string pendingVersion, string currentVersion)
        {
            DesktopSemanticVersion pending = DesktopSemanticVersion.Parse(pendingVersion);
            DesktopSemanticVersion current = DesktopSemanticVersion.Parse(currentVersion);
            return pending.CompareTo(current) > 0;
        }

        private static bool IsInstallerValid(DesktopPendingUpdate update)
        {
            if (string.IsNullOrWhiteSpace(update.InstallerPath) || !File.Exists(update.InstallerPath))
            {
                return false;
            }

            var fileInfo = new FileInfo(update.InstallerPath);
            if (fileInfo.Length != update.SizeBytes)
            {
                return false;
            }

            using FileStream stream = File.OpenRead(update.InstallerPath);
            using var sha256 = SHA256.Create();
            string hash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            return string.Equals(hash, update.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }
}
