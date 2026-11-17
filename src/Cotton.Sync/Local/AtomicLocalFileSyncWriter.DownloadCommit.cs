// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Local
{
    public partial class AtomicLocalFileSyncWriter
    {
        private static void CleanupDownloadAttempt(
            bool moved, string temporaryPath, string? previousPath, string? preservationRoot)
        {
            if (!moved && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (previousPath is not null && preservationRoot is not null && !File.Exists(previousPath))
            {
                CleanupEmptyPreservationDirectories(previousPath, preservationRoot);
            }
        }

        private static void ReplaceDownload(string temporaryPath, string previousPath, string targetPath, string normalizedPath)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (Directory.Exists(targetPath))
                    {
                        throw new IOException("The local download target is a directory.");
                    }

                    LinuxAtomicFileCommit.MoveNoReplace(temporaryPath, previousPath);
                    LinuxAtomicFileCommit.Exchange(previousPath, targetPath);
                }
                else
                {
                    File.Replace(temporaryPath, targetPath, previousPath);
                }
            }
            catch (IOException exception)
            {
                Trace.TraceWarning("Could not replace local download target '{0}': {1}", normalizedPath, exception.Message);
                throw new LocalFileUnavailableException(normalizedPath, targetPath, exception);
            }
        }

        private static void CommitNewDownload(string temporaryPath, string targetPath, string normalizedPath)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    LinuxAtomicFileCommit.MoveNoReplace(temporaryPath, targetPath);
                }
                else
                {
                    File.Move(temporaryPath, targetPath, overwrite: false);
                }
            }
            catch (IOException exception) when (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                Trace.TraceWarning("Local download target '{0}' appeared before commit: {1}", normalizedPath, exception.Message);
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    targetPath,
                    "a local item appeared while its remote version was downloading.");
            }
            catch (IOException exception) when (OperatingSystem.IsLinux())
            {
                Trace.TraceWarning("Could not commit local download target '{0}': {1}", normalizedPath, exception.Message);
                throw new LocalFileUnavailableException(normalizedPath, targetPath, exception);
            }
        }
    }
}
