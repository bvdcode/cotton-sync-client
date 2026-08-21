// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            FileDesiredAccess dwDesiredAccess,
            FileShareMode dwShareMode,
            IntPtr lpSecurityAttributes,
            FileCreationDisposition dwCreationDisposition,
            FileFlagsAndAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            FileInfoByHandleClass fileInformationClass,
            out FileAttributeTagInfo lpFileInformation,
            uint dwBufferSize);

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfRegisterSyncRoot(
            string SyncRootPath,
            ref CfSyncRegistration Registration,
            ref CfSyncPolicies Policies,
            CfRegisterFlags RegisterFlags);

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfUnregisterSyncRoot(string SyncRootPath);

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfCreatePlaceholders(
            string BaseDirectoryPath,
            [In, Out] CfPlaceholderCreateInfo[] PlaceholderArray,
            uint PlaceholderCount,
            CfCreateFlags CreateFlags,
            out uint EntriesProcessed);

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfConnectSyncRoot(
            string SyncRootPath,
            [In] CfCallbackRegistration[] CallbackTable,
            IntPtr CallbackContext,
            CfConnectFlags ConnectFlags,
            out long ConnectionKey);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfDisconnectSyncRoot(long ConnectionKey);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfConvertToPlaceholder(
            IntPtr FileHandle,
            IntPtr FileIdentity,
            uint FileIdentityLength,
            CfConvertFlags ConvertFlags,
            IntPtr ConvertUsn,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfSetPinState(
            IntPtr FileHandle,
            CfPinState PinState,
            CfSetPinFlags PinFlags,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfSetInSyncState(
            IntPtr FileHandle,
            CfInSyncState InSyncState,
            CfSetInSyncFlags InSyncFlags,
            IntPtr InSyncUsn);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern WindowsCloudFilesPlaceholderState CfGetPlaceholderStateFromFileInfo(
            ref FileAttributeTagInfo infoBuffer,
            FileInfoByHandleClass infoClass);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfGetPlaceholderInfo(
            IntPtr FileHandle,
            CfPlaceholderInfoClass InfoClass,
            IntPtr InfoBuffer,
            uint InfoBufferLength,
            out uint ReturnedLength);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfExecute(
            ref CfOperationInfo OpInfo,
            ref CfOperationTransferDataParameters OpParams);

        [DllImport("CldApi.dll", ExactSpelling = true, EntryPoint = "CfExecute")]
        private static extern int CfExecuteAckDehydrate(
            ref CfOperationInfo OpInfo,
            ref CfOperationAckDehydrateParameters OpParams);

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfOpenFileWithOplock(
            string FilePath,
            CfOpenFileFlags Flags,
            out IntPtr ProtectedHandle);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfDehydratePlaceholder(
            IntPtr FileHandle,
            long StartingOffset,
            long Length,
            CfDehydrateFlags DehydrateFlags,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfHydratePlaceholder(
            IntPtr FileHandle,
            long StartingOffset,
            long Length,
            CfHydrateFlags HydrateFlags,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern int CfUpdatePlaceholder(
            IntPtr FileHandle,
            ref CfFsMetadata FsMetadata,
            IntPtr FileIdentity,
            uint FileIdentityLength,
            IntPtr DehydrateRangeArray,
            uint DehydrateRangeCount,
            CfUpdateFlags UpdateFlags,
            IntPtr UpdateUsn,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", EntryPoint = "CfUpdatePlaceholder", ExactSpelling = true)]
        private static extern int CfUpdatePlaceholderIdentity(
            IntPtr FileHandle,
            IntPtr FsMetadata,
            IntPtr FileIdentity,
            uint FileIdentityLength,
            IntPtr DehydrateRangeArray,
            uint DehydrateRangeCount,
            CfUpdateFlags UpdateFlags,
            IntPtr UpdateUsn,
            IntPtr Overlapped);

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern void CfCloseHandle(IntPtr FileHandle);
    }
}
