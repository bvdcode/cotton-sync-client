// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesReparsePointProbe
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

        public static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        public static bool IsCloudFilesReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            if (OperatingSystem.IsWindows() && TryReadReparseTag(path, out uint reparseTag))
            {
                return IsCloudFilesReparseTag(reparseTag);
            }

            return HasRawAttribute(attributes, FileAttributeRecallOnOpen)
                || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                || (attributes & FileAttributes.Offline) != 0;
        }

        public static string CreateOpenPath(string fullPath)
        {
            return WindowsNativePath.ToWin32FilePath(fullPath);
        }

        public static uint CreateOpenFlags(string fullPath)
        {
            uint flags = FileFlagOpenReparsePoint;
            if (Directory.Exists(fullPath))
            {
                flags |= FileFlagBackupSemantics;
            }

            return flags;
        }

        private static bool IsCloudFilesReparseTag(uint reparseTag)
        {
            return (reparseTag & ReparseTagCloudFamilyMask) == ReparseTagCloudFamily
                && (reparseTag & 0xFF) == ReparseTagCloudLowByte;
        }

        private static bool HasRawAttribute(FileAttributes attributes, int flag)
        {
            return (((int)attributes) & flag) == flag;
        }

        private static bool TryReadReparseTag(string fullPath, out uint reparseTag)
        {
            reparseTag = 0;
            using SafeFileHandle handle = CreateFile(
                CreateOpenPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                CreateOpenFlags(fullPath),
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

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
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
