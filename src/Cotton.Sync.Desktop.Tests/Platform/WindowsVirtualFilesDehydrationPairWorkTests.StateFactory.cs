// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        private static WindowsVirtualFileDiskState CreateUnpinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributeUnpinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedRemoteOnlyDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | FileAttributes.Offline
                | (FileAttributes)FileAttributePinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedRemoteOnlyDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | FileAttributes.Offline
                | (FileAttributes)FileAttributeUnpinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributeUnpinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedRegularFileDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 24,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateNeutralHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateMaterializedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateNeutralDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static SyncPairSettings CreateVirtualFilesPair()
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Desktop",
                LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-root"),
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                RemoteDisplayPath = "/Desktop",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }

        private static SyncStateEntry CreatePlaceholderState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RemoteNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                RemoteContentHash = "remote-hash",
                RemoteSizeBytes = 12,
                PlaceholderIdentity = [1, 2, 3],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
            };
        }

        private static SyncStateEntry CreateDirectoryState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
            };
        }
    }
}
