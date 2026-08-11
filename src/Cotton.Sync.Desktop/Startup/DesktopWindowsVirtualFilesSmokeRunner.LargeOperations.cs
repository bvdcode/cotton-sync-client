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
    {        private static int GetLargeTreePlaceholderCount(DesktopStartupOptions startupOptions)
        {
            return startupOptions.WindowsVirtualFilesSmokePlaceholderCount ?? DefaultLargeTreePlaceholderCount;
        }

        private static string CreateLargeTreeFileName(int index)
        {
            return "file-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".txt";
        }

        private static IReadOnlyList<RemoteFileSnapshot> CreateLargeTreeRemoteFiles(
            SyncPairSettings syncPair,
            int largeTreePlaceholderCount)
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            List<RemoteFileSnapshot> remoteFiles = new(largeTreePlaceholderCount);
            for (int index = 0; index < largeTreePlaceholderCount; index++)
            {
                string relativePath = LargeTreeDirectoryName
                    + "/file-"
                    + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                    + ".txt";
                RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(
                    syncPair,
                    relativePath,
                    expectedContent.LongLength,
                    expectedHash);
                ApplyLargeSmokeRemoteIdentity(request.RemoteFile, index);
                remoteFiles.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = request.RemoteFile,
                });
            }

            return remoteFiles;
        }

        private static RemoteDirectorySnapshot CreateLargeTreeRemoteDirectory(SyncPairSettings syncPair)
        {
            RemoteDirectoryMaterializationRequest request = CreateDirectoryRequest(syncPair, LargeTreeDirectoryName);
            return new RemoteDirectorySnapshot
            {
                RelativePath = request.RelativePath,
                Node = request.RemoteDirectory,
            };
        }

        private static async Task<int> VerifyPairDeletedAsync(
            ISyncPairSettingsStore syncPairs,
            ISyncStateStore stateStore,
            SyncPairSettings syncPair,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            int failures = 0;
            IReadOnlyList<SyncPairSettings> remainingPairs =
                await syncPairs.ListAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SyncStateEntry> remainingEntries =
                await stateStore.LoadPairAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
            SyncChangeCursor remainingCursor =
                await stateStore.GetChangeCursorAsync(syncPair.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (remainingPairs.Count == 0 && remainingEntries.Count == 0 && remainingCursor.LastCursor == 0)
            {
                await output.WriteLineAsync(
                    FormatCheck(true, "Pair settings, sync-state rows, and change cursor were removed.")
                    + " settings="
                    + remainingPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", entries="
                    + remainingEntries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", cursor="
                    + remainingCursor.LastCursor.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
            }
            else
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, "Pair deletion left settings or sync-state behind.")
                    + " settings="
                    + remainingPairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", entries="
                    + remainingEntries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", cursor="
                    + remainingCursor.LastCursor.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
            }

            return failures;
        }

        private static SyncApplicationService CreateDeletionSmokeApplication(
            ISyncPairSettingsStore syncPairs,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles)
        {
            return new SyncApplicationService(
                syncPairs,
                NoopSyncPairPrerequisiteValidator.Instance,
                new NoopAppPreferencesStore(),
                NoopAuthFlow.Instance,
                NoopAppCodeBrowserAuthFlow.Instance,
                new NoopSyncSupervisor(),
                NoopPlatformCommandService.Instance,
                NullLocalChangeSyncCoordinator.Instance,
                NullRemoteChangeSyncCoordinator.Instance,
                NullPeriodicSyncCoordinator.Instance,
                syncStateStore: stateStore,
                syncPairDeletionHandler: new WindowsCloudFilesSyncPairDeletionHandler(
                    cloudFiles,
                    syncStateStore: stateStore));
        }

        private static byte[] CreateLargeSmokePlaceholderIdentity(int index)
        {
            byte[] identity = new byte[1024];
            for (int offset = 0; offset < identity.Length; offset++)
            {
                identity[offset] = (byte)((index + (offset * 17)) & 0xff);
            }

            return identity;
        }

        private static void ApplyLargeSmokeRemoteIdentity(NodeFileManifestDto remoteFile, int index)
        {
            remoteFile.Id = CreateLargeSmokeGuid(0x33, index);
            remoteFile.FileManifestId = CreateLargeSmokeGuid(0x55, index);
            remoteFile.OriginalNodeFileId = remoteFile.Id;
            remoteFile.ETag = "vfs-smoke-etag-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Guid CreateLargeSmokeGuid(byte marker, int index)
        {
            byte[] bytes = new byte[16];
            bytes[0] = marker;
            byte[] indexBytes = BitConverter.GetBytes(index);
            Array.Copy(indexBytes, 0, bytes, 12, indexBytes.Length);
            return new Guid(bytes);
        }

        private static DesktopRuntimeHealthSnapshot CreateRuntimeHealthSnapshot()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new DesktopRuntimeHealthSnapshot(
                process.Id,
                process.ProcessName,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count,
                process.HandleCount);
        }

        private static void ForceFullCollection()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private static string FormatRuntimeHealth(DesktopRuntimeHealthSnapshot runtimeHealth)
        {
            return "workingSetBytes="
                + runtimeHealth.WorkingSetBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";privateMemoryBytes="
                + FormatNullable(runtimeHealth.PrivateMemoryBytes)
                + ";threadCount="
                + FormatNullable(runtimeHealth.ThreadCount)
                + ";handleCount="
                + FormatNullable(runtimeHealth.HandleCount);
        }

        private static string FormatNullable(long? value)
        {
            return value.HasValue
                ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unavailable";
        }

        private static string FormatNullable(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unavailable";
        }

        private class RecordingSyncRunProgress : IProgress<SyncRunProgress>
        {
            private readonly object _sync = new();
            private readonly List<SyncRunProgress> _items = [];

            public void Report(SyncRunProgress value)
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_sync)
                {
                    _items.Add(value);
                }
            }

            public IReadOnlyList<SyncRunProgress> Snapshot()
            {
                lock (_sync)
                {
                    return _items.ToArray();
                }
            }
        }
}
}
