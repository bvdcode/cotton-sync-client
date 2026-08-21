// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        public void ConvertToPlaceholder(string filePath, byte[] fileIdentity, bool isDirectory, bool markInSync)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(fileIdentity);
            FileFlagsAndAttributes flags = FileFlagsAndAttributes.OpenReparsePoint;
            if (isDirectory)
            {
                flags |= FileFlagsAndAttributes.BackupSemantics;
            }

            using SafeFileHandle handle = CreateFile(
                WindowsNativePath.ToWin32FilePath(filePath),
                FileDesiredAccess.WriteData | FileDesiredAccess.WriteAttributes,
                FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete,
                IntPtr.Zero,
                FileCreationDisposition.OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(CreateFile),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            PinnedBuffer identity = PinnedBuffer.Pin(fileIdentity);
            try
            {
                CfConvertFlags convertFlags = markInSync
                    ? CfConvertFlags.MarkInSync
                    : CfConvertFlags.None;
                int result = CfConvertToPlaceholder(
                    handle.DangerousGetHandle(),
                    identity.Pointer,
                    identity.Length,
                    convertFlags,
                    IntPtr.Zero,
                    IntPtr.Zero);
                ThrowIfFailed(result, nameof(CfConvertToPlaceholder));
            }
            finally
            {
                identity.Dispose();
            }
        }

        public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            FileFlagsAndAttributes flags = FileFlagsAndAttributes.OpenReparsePoint;
            if (Directory.Exists(filePath))
            {
                flags |= FileFlagsAndAttributes.BackupSemantics;
            }

            using SafeFileHandle handle = CreateFile(
                WindowsNativePath.ToWin32FilePath(filePath),
                FileDesiredAccess.ReadData,
                FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete,
                IntPtr.Zero,
                FileCreationDisposition.OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(CreateFile),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            int result = CfSetPinState(
                handle.DangerousGetHandle(),
                (CfPinState)pinState,
                CfSetPinFlags.None,
                IntPtr.Zero);
            ThrowIfFailed(result, nameof(CfSetPinState));
        }

        public void SetInSyncState(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using SafeFileHandle handle = CreateFile(
                WindowsNativePath.ToWin32FilePath(filePath),
                FileDesiredAccess.WriteAttributes,
                FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete,
                IntPtr.Zero,
                FileCreationDisposition.OpenExisting,
                FileFlagsAndAttributes.OpenReparsePoint | FileFlagsAndAttributes.BackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(CreateFile),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            int result = CfSetInSyncState(
                handle.DangerousGetHandle(),
                CfInSyncState.InSync,
                CfSetInSyncFlags.None,
                IntPtr.Zero);
            ThrowIfFailed(result, nameof(CfSetInSyncState));
        }

        public WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using SafeFileHandle handle = CreateFile(
                WindowsNativePath.ToWin32FilePath(filePath),
                FileDesiredAccess.ReadAttributes,
                FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete,
                IntPtr.Zero,
                FileCreationDisposition.OpenExisting,
                FileFlagsAndAttributes.OpenReparsePoint | FileFlagsAndAttributes.BackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(CreateFile),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out FileAttributeTagInfo attributeTagInfo,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(GetFileInformationByHandleEx),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            WindowsCloudFilesPlaceholderState state = CfGetPlaceholderStateFromFileInfo(
                ref attributeTagInfo,
                FileInfoByHandleClass.FileAttributeTagInfo);
            if (state == WindowsCloudFilesPlaceholderState.Invalid)
            {
                throw new WindowsCloudFilesNativeException(
                    nameof(CfGetPlaceholderStateFromFileInfo),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            return state;
        }

        public byte[] GetPlaceholderIdentity(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using SafeFileHandle handle = OpenPlaceholderForRead(filePath);
            int bufferLength = PlaceholderBasicInfoIdentityOffset
                + WindowsCloudFilesPlaceholderIdentity.MaximumIdentityLength;
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int result = CfGetPlaceholderInfo(
                    handle.DangerousGetHandle(),
                    CfPlaceholderInfoClass.Basic,
                    buffer,
                    (uint)bufferLength,
                    out uint returnedLength);
                ThrowIfFailed(result, nameof(CfGetPlaceholderInfo));
                int identityLength = Marshal.ReadInt32(buffer, PlaceholderBasicInfoIdentityLengthOffset);
                if (identityLength < 0
                    || identityLength > WindowsCloudFilesPlaceholderIdentity.MaximumIdentityLength
                    || returnedLength < PlaceholderBasicInfoIdentityOffset + identityLength)
                {
                    throw new InvalidOperationException("Windows Cloud Files returned an invalid placeholder identity length.");
                }

                byte[] identity = new byte[identityLength];
                Marshal.Copy(
                    IntPtr.Add(buffer, PlaceholderBasicInfoIdentityOffset),
                    identity,
                    0,
                    identityLength);
                return identity;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void UpdatePlaceholderIdentity(string filePath, byte[] placeholderIdentity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(placeholderIdentity);
            if (placeholderIdentity.Length > WindowsCloudFilesPlaceholderIdentity.MaximumIdentityLength)
            {
                throw new ArgumentException("Cloud Files placeholder identity exceeds the Windows 4 KB limit.", nameof(placeholderIdentity));
            }

            int openResult = CfOpenFileWithOplock(
                WindowsNativePath.ToWin32FilePath(filePath),
                CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess,
                out IntPtr protectedHandle);
            ThrowIfFailed(openResult, nameof(CfOpenFileWithOplock));
            try
            {
                PinnedBuffer identity = PinnedBuffer.Pin(placeholderIdentity);
                try
                {
                    int result = CfUpdatePlaceholderIdentity(
                        protectedHandle,
                        IntPtr.Zero,
                        identity.Pointer,
                        identity.Length,
                        IntPtr.Zero,
                        0,
                        CfUpdateFlags.VerifyInSync | CfUpdateFlags.MarkInSync,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    ThrowIfFailed(result, nameof(CfUpdatePlaceholder));
                }
                finally
                {
                    identity.Dispose();
                }
            }
            finally
            {
                CfCloseHandle(protectedHandle);
            }
        }

        private static SafeFileHandle OpenPlaceholderForRead(string filePath)
        {
            SafeFileHandle handle = CreateFile(
                WindowsNativePath.ToWin32FilePath(filePath),
                FileDesiredAccess.ReadAttributes,
                FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete,
                IntPtr.Zero,
                FileCreationDisposition.OpenExisting,
                FileFlagsAndAttributes.OpenReparsePoint | FileFlagsAndAttributes.BackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new WindowsCloudFilesNativeException(
                    nameof(CreateFile),
                    HResultFromWin32(Marshal.GetLastWin32Error()));
            }

            return handle;
        }
    }
}
