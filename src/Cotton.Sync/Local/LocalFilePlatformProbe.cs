// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cotton.Sync.Local
{
    internal static class LocalFilePlatformProbe
    {
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const uint FileShareDelete = 0x00000004;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FsctlGetReparsePoint = 0x000900A8;
        private const uint OpenExisting = 3;
        private const int ReparseDataBufferSize = 16 * 1024;
        private const uint ReparseTagCloudFamily = 0x9000001A;
        private const uint ReparseTagCloudFamilyMask = 0xF00000FF;
        private const uint ReparseTagCloudLowByte = 0x1A;

        public static bool IsCloudFilesPlaceholder(FileSystemInfo info, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            if (OperatingSystem.IsWindows()
                && TryReadReparseTag(info.FullName, info is DirectoryInfo, out uint reparseTag))
            {
                return IsCloudFilesReparseTag(reparseTag);
            }

            return IsCloudFilesOnlineOnlyAttributes(attributes);
        }

        public static bool ShouldIncludeScopedDirectory(
            FileAttributes attributes,
            bool isCloudFilesPlaceholder)
        {
            return (attributes & FileAttributes.ReparsePoint) == 0 || isCloudFilesPlaceholder;
        }

        public static bool IsCloudFilesReparseTag(uint reparseTag)
        {
            return (reparseTag & ReparseTagCloudFamilyMask) == ReparseTagCloudFamily
                && (reparseTag & 0xFF) == ReparseTagCloudLowByte;
        }

        public static bool IsCloudFilesOnlineOnlyAttributes(FileAttributes attributes)
        {
            return HasRawAttribute(attributes, FileAttributeRecallOnOpen)
                || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                || (attributes & FileAttributes.Offline) != 0;
        }

        public static string CreateReparseTagOpenPath(string fullPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return fullPath;
            }

            if (fullPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\?\GLOBALROOT" + fullPath;
            }

            string normalizedPath = Path.GetFullPath(fullPath);
            if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + normalizedPath.TrimStart('\\');
            }

            return @"\\?\" + normalizedPath;
        }

        public static void ValidatePermissions(FileInfo file, string relativePath)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            UnixFileMode readMask = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            try
            {
                if ((File.GetUnixFileMode(file.FullName) & readMask) == 0)
                {
                    throw new LocalFilePermissionDeniedException(
                        relativePath,
                        file.FullName,
                        "the file has no Unix read permission bits.");
                }
            }
            catch (LocalFilePermissionDeniedException)
            {
                throw;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalFilePermissionDeniedException(relativePath, file.FullName, exception);
            }
            catch (IOException exception)
            {
                throw new LocalFileUnavailableException(relativePath, file.FullName, exception);
            }
        }

        private static bool TryReadReparseTag(string fullPath, bool isDirectory, out uint reparseTag)
        {
            reparseTag = 0;
            uint openFlags = FileFlagOpenReparsePoint | (isDirectory ? FileFlagBackupSemantics : 0);
            using SafeFileHandle handle = CreateFile(
                CreateReparseTagOpenPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                openFlags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            byte[] buffer = new byte[ReparseDataBufferSize];
            if (!DeviceIoControl(
                    handle,
                    FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    buffer.Length,
                    out int bytesReturned,
                    IntPtr.Zero)
                || bytesReturned < sizeof(uint))
            {
                return false;
            }

            reparseTag = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            return true;
        }

        private static bool HasRawAttribute(FileAttributes attributes, int rawAttribute)
        {
            return (((int)attributes) & rawAttribute) == rawAttribute;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inBuffer,
            int inBufferSize,
            byte[] outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
