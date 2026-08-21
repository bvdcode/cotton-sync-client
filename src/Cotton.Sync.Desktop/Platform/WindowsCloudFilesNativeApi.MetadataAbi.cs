// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CfPlaceholderCreateInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string RelativeFileName;

            public CfFsMetadata FsMetadata;

            public IntPtr FileIdentity;

            public uint FileIdentityLength;

            public CfPlaceholderCreateFlags Flags;

            public int Result;

            public long CreateUsn;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CfPlaceholderBasicInfo
        {
            public uint PinState;

            public uint InSyncState;

            public long FileId;

            public long SyncRootFileId;

            public uint FileIdentityLength;

            public byte FileIdentity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CfFsMetadata
        {
            private readonly FileBasicInfo _basicInfo;

            private readonly long _fileSize;

            private CfFsMetadata(FileBasicInfo basicInfo, long fileSize)
            {
                _basicInfo = basicInfo;
                _fileSize = fileSize;
            }

            public static CfFsMetadata CreateFile(long fileSize, DateTime createdAtUtc, DateTime updatedAtUtc)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
                long createdAtFileTime = ToFileTimeUtc(createdAtUtc);
                long updatedAtFileTime = ToFileTimeUtc(updatedAtUtc);
                return new CfFsMetadata(
                    new FileBasicInfo
                    {
                        CreationTime = createdAtFileTime,
                        LastAccessTime = updatedAtFileTime,
                        LastWriteTime = updatedAtFileTime,
                        ChangeTime = updatedAtFileTime,
                        FileAttributes = (uint)FileAttributes.Archive,
                    },
                    fileSize);
            }

            public static CfFsMetadata CreateDirectory(DateTime createdAtUtc, DateTime updatedAtUtc)
            {
                long createdAtFileTime = ToFileTimeUtc(createdAtUtc);
                long updatedAtFileTime = ToFileTimeUtc(updatedAtUtc);
                return new CfFsMetadata(
                    new FileBasicInfo
                    {
                        CreationTime = createdAtFileTime,
                        LastAccessTime = updatedAtFileTime,
                        LastWriteTime = updatedAtFileTime,
                        ChangeTime = updatedAtFileTime,
                        FileAttributes = (uint)FileAttributes.Directory,
                    },
                    0);
            }

            private static long ToFileTimeUtc(DateTime value)
            {
                DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
                DateTime minFileTimeUtc = DateTime.FromFileTimeUtc(0);
                return (utc < minFileTimeUtc ? minFileTimeUtc : utc).ToFileTimeUtc();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInfo
        {
            public long CreationTime;

            public long LastAccessTime;

            public long LastWriteTime;

            public long ChangeTime;

            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;

            public uint ReparseTag;
        }
    }
}
