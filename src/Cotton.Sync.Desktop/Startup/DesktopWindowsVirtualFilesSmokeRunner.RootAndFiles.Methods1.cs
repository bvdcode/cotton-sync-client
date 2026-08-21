// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static string ResolveSmokeRoot(string? configuredRoot)
        {
            return string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.GetFullPath(DefaultSmokeRoot)
                : Path.GetFullPath(configuredRoot.Trim());
        }

        private static string? ValidateSmokeRoot(string rootPath)
        {
            if (!Path.IsPathFullyQualified(rootPath))
            {
                return "Windows virtual-files smoke root must be an absolute path.";
            }

            WindowsVirtualFilesRootSafetyResult safety = new WindowsVirtualFilesRootSafetyPolicy().Validate(rootPath);
            if (!safety.IsSafe)
            {
                return "Windows virtual-files smoke root is unsafe: " + safety.Details;
            }

            string defaultParentRoot = Path.GetFullPath(DefaultSmokeParentRoot);
            StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            string normalizedRoot = NormalizeFullPath(rootPath);
            string normalizedDefaultParentRoot = NormalizeFullPath(defaultParentRoot);
            if (IsChildOf(normalizedRoot, normalizedDefaultParentRoot, comparison))
            {
                return null;
            }

            string? runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
            if (!string.IsNullOrWhiteSpace(runnerTemp))
            {
                string normalizedRunnerTemp = NormalizeFullPath(runnerTemp);
                if (IsChildOf(normalizedRoot, normalizedRunnerTemp, comparison))
                {
                    return null;
                }
            }

            if (string.Equals(normalizedRoot, normalizedDefaultParentRoot, comparison))
            {
                return "Windows virtual-files smoke refuses to use the default parent root directly.";
            }

            return "Windows virtual-files smoke refuses to touch paths outside an allowed smoke root.";
        }

        private static bool IsChildOf(
            string candidate,
            string root,
            StringComparison comparison)
        {
            return !string.Equals(candidate, root, comparison)
                && candidate.StartsWith(EnsureTrailingSeparator(root), comparison);
        }

        private static async Task<string?> PrepareSmokeRootEnvironmentAsync(
            string rootPath,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string? driveRoot = Path.GetPathRoot(rootPath);
            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                return "Windows virtual-files smoke root drive could not be resolved.";
            }

            if (Directory.Exists(driveRoot))
            {
                await output
                    .WriteLineAsync(FormatCheck(true, "Isolated QA drive is available.") + " drive=" + driveRoot)
                    .ConfigureAwait(false);
                return null;
            }

            string driveName = driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(driveName, "S:", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows virtual-files smoke drive is unavailable: " + driveName;
            }

            string backingDirectory = Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
                "CottonSyncSmokeDrive");
            try
            {
                Directory.CreateDirectory(backingDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "Windows virtual-files smoke could not prepare the isolated QA drive backing directory: "
                    + CleanSingleLine(exception.Message);
            }

            SubstResult subst = await RunSubstAsync(driveName, backingDirectory, cancellationToken).ConfigureAwait(false);
            if (subst.ExitCode != 0)
            {
                return "Windows virtual-files smoke could not create the isolated QA drive: "
                    + CleanSingleLine(subst.Error.Length == 0 ? subst.Output : subst.Error);
            }

            if (!Directory.Exists(driveRoot))
            {
                return "Windows virtual-files smoke created the isolated QA drive mapping, but the drive is still unavailable.";
            }

            await output
                .WriteLineAsync(FormatCheck(true, "Isolated QA drive prepared.") + " drive=" + driveRoot)
                .ConfigureAwait(false);
            return null;
        }

        private static async Task<SubstResult> RunSubstAsync(
            string driveName,
            string backingDirectory,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Path.Combine(Environment.SystemDirectory, "subst.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(driveName);
            startInfo.ArgumentList.Add(backingDirectory);

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SubstResult(1, string.Empty, "subst.exe could not be started.");
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new SubstResult(
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }

        private static void PrepareRoot(string rootPath)
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            Directory.CreateDirectory(rootPath);
        }

        private static void TryUnregisterExistingRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine("Info: previous Cloud Files registration was unregistered before smoke.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine("Info: no previous Cloud Files registration cleanup was confirmed: " + CleanSingleLine(exception.Message));
            }
        }

        private static int TryUnregisterSmokeRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine(FormatCheck(true, "Cloud Files sync root unregistered after smoke.") + " root=" + syncPair.LocalRootPath);
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine(
                    FormatCheck(false, "Cloud Files sync root cleanup failed.")
                    + " "
                    + CleanSingleLine(exception.Message));
                return 1;
            }
        }

        private static SyncPairSettings CreateSyncPair(string rootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Cotton Sync VFS smoke",
                LocalRootPath = rootPath,
                RemoteDisplayPath = "/CottonSyncQa/WindowsVirtualFilesSmoke",
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncPairSettings CreateDesktopRootSyncPair(string rootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("12121212-1212-1212-1212-121212121212"),
                DisplayName = "Desktop",
                LocalRootPath = rootPath,
                RemoteDisplayPath = "/Desktop",
                RemoteRootNodeId = Guid.Parse("23232323-2323-2323-2323-232323232323"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncPairSettings CreateDesktopSessionRestoreSyncPair(string rootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("13131313-1313-1313-1313-131313131313"),
                DisplayName = "Desktop",
                LocalRootPath = rootPath,
                RemoteDisplayPath = "/Desktop",
                RemoteRootNodeId = Guid.Parse("24242424-2424-2424-2424-242424242424"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static RemoteFilePlaceholderRequest CreatePlaceholderRequest(
            SyncPairSettings syncPair,
            string relativePath,
            long sizeBytes,
            string contentHash)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = sizeBytes,
                    ContentHash = contentHash,
                    ETag = "vfs-smoke-etag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private static void ApplyShellShareLinkRemoteIdentity(NodeFileManifestDto remoteFile, int identityIndex)
        {
            ArgumentNullException.ThrowIfNull(remoteFile);
            remoteFile.Id = Guid.CreateVersion7();
            remoteFile.NodeId = Guid.CreateVersion7();
            remoteFile.FileManifestId = Guid.CreateVersion7();
            remoteFile.OriginalNodeFileId = Guid.CreateVersion7();
            remoteFile.ETag = "vfs-shell-share-link-etag-" + identityIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static RemoteDirectoryMaterializationRequest CreateDirectoryRequest(
            SyncPairSettings syncPair,
            string relativePath)
        {
            string normalizedPath = SyncPath.Normalize(relativePath);
            return new RemoteDirectoryMaterializationRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                normalizedPath,
                new NodeDto
                {
                    Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    ParentId = syncPair.RemoteRootNodeId,
                    Name = normalizedPath.Split('/')[^1],
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
        }

        private static async Task<int> VerifyCloudFilesInSyncStateAsync(
            TextWriter output,
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            string? relativePath,
            string label,
            bool allowPartialDirectory = false)
        {
            try
            {
                WindowsCloudFilesPlaceholderState state = cloudFiles.GetPlaceholderState(syncPair, relativePath);
                bool passed = state.HasFlag(WindowsCloudFilesPlaceholderState.InSync)
                    && (allowPartialDirectory || !state.HasFlag(WindowsCloudFilesPlaceholderState.Partial));
                await output.WriteLineAsync(
                        FormatCheck(passed, label)
                        + " state="
                        + state)
                    .ConfigureAwait(false);
                return passed ? 0 : 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync(
                        FormatCheck(false, label)
                        + " "
                        + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
        }
    }
}
