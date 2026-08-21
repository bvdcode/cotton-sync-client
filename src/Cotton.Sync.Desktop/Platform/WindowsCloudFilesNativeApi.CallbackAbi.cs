// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        private enum CfCallbackType : uint
        {
            FetchData = 0,
            CancelFetchData = 2,
            NotifyDehydrate = 7,
            NotifyDehydrateCompletion = 8,
            None = 0xffffffff,
        }

        private enum CfOperationType : uint
        {
            TransferData = 0,
            AckDehydrate = 5,
        }

        [Flags]
        private enum CfOperationTransferDataFlags : uint
        {
            None = 0x00000000,
        }

        [Flags]
        private enum CfOperationAckDehydrateFlags : uint
        {
            None = 0x00000000,
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CfCallback(IntPtr callbackInfo, IntPtr callbackParameters);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CfCallbackRegistration
        {
            public CfCallbackRegistration(CfCallbackType type, IntPtr callback)
            {
                Type = type;
                Callback = callback;
            }

            public readonly CfCallbackType Type;

            public readonly IntPtr Callback;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CfCallbackInfo
        {
            public uint StructSize;

            public long ConnectionKey;

            public IntPtr CallbackContext;

            public IntPtr VolumeGuidName;

            public IntPtr VolumeDosName;

            public uint VolumeSerialNumber;

            public long SyncRootFileId;

            public IntPtr SyncRootIdentity;

            public uint SyncRootIdentityLength;

            public long FileId;

            public long FileSize;

            public IntPtr FileIdentity;

            public uint FileIdentityLength;

            public IntPtr NormalizedPath;

            public long TransferKey;

            public byte PriorityHint;

            public IntPtr CorrelationVector;

            public IntPtr ProcessInfo;

            public long RequestKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CfProcessInfo
        {
            public uint StructSize;

            public uint ProcessId;

            public IntPtr ImagePath;

            public IntPtr PackageName;

            public IntPtr ApplicationId;

            public IntPtr CommandLine;

            public uint SessionId;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfCallbackFetchDataParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public uint Flags;

            [FieldOffset(16)]
            public long RequiredFileOffset;

            [FieldOffset(24)]
            public long RequiredLength;

            [FieldOffset(32)]
            public long OptionalFileOffset;

            [FieldOffset(40)]
            public long OptionalLength;

            [FieldOffset(48)]
            public long LastDehydrationTime;

            [FieldOffset(56)]
            public int LastDehydrationReason;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfCallbackCancelFetchDataParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public uint Flags;

            [FieldOffset(16)]
            public long FileOffset;

            [FieldOffset(24)]
            public long Length;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfCallbackDehydrateParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public uint Flags;

            [FieldOffset(12)]
            public int Reason;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfCallbackDehydrateCompletionParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public uint Flags;

            [FieldOffset(12)]
            public int Reason;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CfOperationInfo
        {
            public uint StructSize;

            public CfOperationType Type;

            public long ConnectionKey;

            public long TransferKey;

            public IntPtr CorrelationVector;

            public IntPtr SyncStatus;

            public long RequestKey;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfOperationTransferDataParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public CfOperationTransferDataFlags Flags;

            [FieldOffset(12)]
            public int CompletionStatus;

            [FieldOffset(16)]
            public IntPtr Buffer;

            [FieldOffset(24)]
            public long Offset;

            [FieldOffset(32)]
            public long Length;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct CfOperationAckDehydrateParameters
        {
            [FieldOffset(0)]
            public uint ParamSize;

            [FieldOffset(8)]
            public CfOperationAckDehydrateFlags Flags;

            [FieldOffset(12)]
            public int CompletionStatus;

            [FieldOffset(16)]
            public IntPtr FileIdentity;

            [FieldOffset(24)]
            public uint FileIdentityLength;
        }
    }
}
