// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesNativeApi : IWindowsCloudFilesNativeApi
    {
        private const int Succeeded = 0;

        public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            Directory.CreateDirectory(registration.LocalRootPath);

            PinnedBuffer syncRootIdentity = PinnedBuffer.Pin(registration.SyncRootIdentity);
            try
            {
                var nativeRegistration = new CfSyncRegistration
                {
                    StructSize = (uint)Marshal.SizeOf<CfSyncRegistration>(),
                    ProviderName = registration.ProviderName,
                    ProviderVersion = registration.ProviderVersion,
                    SyncRootIdentity = syncRootIdentity.Pointer,
                    SyncRootIdentityLength = syncRootIdentity.Length,
                    FileIdentity = IntPtr.Zero,
                    FileIdentityLength = 0,
                    ProviderId = registration.ProviderId,
                };
                CfSyncPolicies policies = CfSyncPolicies.CreateDefault();
                int result = CfRegisterSyncRoot(
                    WindowsNativePath.ToWin32FilePath(registration.LocalRootPath),
                    ref nativeRegistration,
                    ref policies,
                    CfRegisterFlags.Update | CfRegisterFlags.MarkInSyncOnRoot);
                ThrowIfFailed(result, nameof(CfRegisterSyncRoot));
            }
            finally
            {
                syncRootIdentity.Dispose();
            }
        }

        public void UnregisterSyncRoot(string localRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            int result = CfUnregisterSyncRoot(WindowsNativePath.ToWin32FilePath(localRootPath));
            ThrowIfFailed(result, nameof(CfUnregisterSyncRoot));
        }

        public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
        {
            ArgumentNullException.ThrowIfNull(placeholder);
            CreatePlaceholders([placeholder]);
        }

        public void CreatePlaceholders(IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
        {
            ArgumentNullException.ThrowIfNull(placeholders);
            foreach (IGrouping<string, WindowsCloudFilesNativePlaceholder> group in placeholders
                .GroupBy(static placeholder => placeholder.BaseDirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                CreatePlaceholdersInDirectory(group.Key, [.. group]);
            }
        }

        private static void CreatePlaceholdersInDirectory(
            string baseDirectoryPath,
            IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
        {
            Directory.CreateDirectory(baseDirectoryPath);
            var pinnedIdentities = new PinnedBuffer[placeholders.Count];
            try
            {
                var nativePlaceholders = new CfPlaceholderCreateInfo[placeholders.Count];
                for (int index = 0; index < placeholders.Count; index++)
                {
                    WindowsCloudFilesNativePlaceholder placeholder = placeholders[index];
                    pinnedIdentities[index] = PinnedBuffer.Pin(placeholder.FileIdentity);
                    nativePlaceholders[index] = new CfPlaceholderCreateInfo
                    {
                        RelativeFileName = placeholder.RelativeFileName,
                        FsMetadata = placeholder.IsDirectory
                            ? CfFsMetadata.CreateDirectory(placeholder.CreatedAtUtc, placeholder.UpdatedAtUtc)
                            : CfFsMetadata.CreateFile(
                                placeholder.FileSizeBytes,
                                placeholder.CreatedAtUtc,
                                placeholder.UpdatedAtUtc),
                        FileIdentity = pinnedIdentities[index].Pointer,
                        FileIdentityLength = pinnedIdentities[index].Length,
                        Flags = CfPlaceholderCreateFlags.MarkInSync,
                        Result = Succeeded,
                        CreateUsn = 0,
                    };
                }

                int result = CfCreatePlaceholders(
                    WindowsNativePath.ToWin32FilePath(baseDirectoryPath),
                    nativePlaceholders,
                    (uint)nativePlaceholders.Length,
                    CfCreateFlags.StopOnError,
                    out uint entriesProcessed);
                ThrowIfFailed(result, nameof(CfCreatePlaceholders));

                uint processed = Math.Min(entriesProcessed, (uint)nativePlaceholders.Length);
                for (int index = 0; index < processed; index++)
                {
                    ThrowIfFailed(nativePlaceholders[index].Result, nameof(CfCreatePlaceholders));
                }

                if (entriesProcessed != nativePlaceholders.Length)
                {
                    throw new WindowsCloudFilesNativeException(nameof(CfCreatePlaceholders), unchecked((int)0x80004005));
                }
            }
            finally
            {
                foreach (PinnedBuffer pinnedIdentity in pinnedIdentities)
                {
                    pinnedIdentity.Dispose();
                }
            }
        }

        public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
        {
            ArgumentNullException.ThrowIfNull(placeholder);
            string filePath = Path.Combine(placeholder.BaseDirectoryPath, placeholder.RelativeFileName);

            PinnedBuffer fileIdentity = PinnedBuffer.Pin(placeholder.FileIdentity);
            try
            {
                FileFlagsAndAttributes flags = FileFlagsAndAttributes.OpenReparsePoint;
                if (placeholder.IsDirectory)
                {
                    flags |= FileFlagsAndAttributes.BackupSemantics;
                }

                using SafeFileHandle handle = CreateFile(
                    WindowsNativePath.ToWin32FilePath(filePath),
                    placeholder.IsDirectory
                        ? FileDesiredAccess.WriteAttributes
                        : FileDesiredAccess.WriteData,
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

                CfFsMetadata metadata = placeholder.IsDirectory
                    ? CfFsMetadata.CreateDirectory(placeholder.CreatedAtUtc, placeholder.UpdatedAtUtc)
                    : CfFsMetadata.CreateFile(
                        placeholder.FileSizeBytes,
                        placeholder.CreatedAtUtc,
                        placeholder.UpdatedAtUtc);
                int result = CfUpdatePlaceholder(
                    handle.DangerousGetHandle(),
                    ref metadata,
                    fileIdentity.Pointer,
                    fileIdentity.Length,
                    IntPtr.Zero,
                    0,
                    CfUpdateFlags.MarkInSync | CfUpdateFlags.AllowPartial,
                    IntPtr.Zero,
                    IntPtr.Zero);
                ThrowIfFailed(result, nameof(CfUpdatePlaceholder));
            }
            finally
            {
                fileIdentity.Dispose();
            }
        }

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

        public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            Directory.CreateDirectory(request.LocalRootPath);

            var callbackState = new NativeCallbackState(request.CallbackHandler, this);
            int result = CfConnectSyncRoot(
                request.LocalRootPath,
                callbackState.CallbackTable,
                callbackState.Context,
                CfConnectFlags.RequireProcessInfo | CfConnectFlags.BlockSelfImplicitHydration,
                out long connectionKey);
            if (result < Succeeded)
            {
                callbackState.Dispose();
                ThrowIfFailed(result, nameof(CfConnectSyncRoot));
            }

            return new WindowsCloudFilesConnection(
                request.LocalRootPath,
                new WindowsCloudFilesConnectionKey(connectionKey),
                DisconnectSyncRoot,
                callbackState);
        }

        public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
        {
            int result = CfDisconnectSyncRoot(connectionKey.Value);
            ThrowIfFailed(result, nameof(CfDisconnectSyncRoot));
        }

        public void TransferData(WindowsCloudFilesTransferData transfer)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            if (transfer.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transfer), "Cloud Files transfer length cannot be negative.");
            }

            if (transfer.CompletionStatus == WindowsCloudFilesTransferData.StatusSuccess
                && transfer.Length > transfer.Buffer.LongLength)
            {
                throw new ArgumentException("Cloud Files transfer buffer is shorter than the requested transfer length.", nameof(transfer));
            }

            PinnedBuffer buffer = PinnedBuffer.Pin(transfer.Buffer);
            try
            {
                var operationInfo = new CfOperationInfo
                {
                    StructSize = (uint)Marshal.SizeOf<CfOperationInfo>(),
                    Type = CfOperationType.TransferData,
                    ConnectionKey = transfer.ConnectionKey.Value,
                    TransferKey = transfer.TransferKey.Value,
                    CorrelationVector = IntPtr.Zero,
                    SyncStatus = IntPtr.Zero,
                    RequestKey = transfer.RequestKey.Value,
                };
                var parameters = new CfOperationTransferDataParameters
                {
                    ParamSize = (uint)Marshal.SizeOf<CfOperationTransferDataParameters>(),
                    Flags = CfOperationTransferDataFlags.None,
                    CompletionStatus = transfer.CompletionStatus,
                    Buffer = transfer.CompletionStatus == WindowsCloudFilesTransferData.StatusSuccess
                        ? buffer.Pointer
                        : IntPtr.Zero,
                    Offset = transfer.Offset,
                    Length = transfer.Length,
                };

                int result = CfExecute(ref operationInfo, ref parameters);
                ThrowIfFailed(result, nameof(CfExecute));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
        {
            ArgumentNullException.ThrowIfNull(dehydrate);

            PinnedBuffer fileIdentity = PinnedBuffer.Pin(dehydrate.FileIdentity);
            try
            {
                var operationInfo = new CfOperationInfo
                {
                    StructSize = (uint)Marshal.SizeOf<CfOperationInfo>(),
                    Type = CfOperationType.AckDehydrate,
                    ConnectionKey = dehydrate.ConnectionKey.Value,
                    TransferKey = dehydrate.TransferKey.Value,
                    CorrelationVector = IntPtr.Zero,
                    SyncStatus = IntPtr.Zero,
                    RequestKey = dehydrate.RequestKey.Value,
                };
                var parameters = new CfOperationAckDehydrateParameters
                {
                    ParamSize = (uint)Marshal.SizeOf<CfOperationAckDehydrateParameters>(),
                    Flags = CfOperationAckDehydrateFlags.None,
                    CompletionStatus = dehydrate.CompletionStatus,
                    FileIdentity = fileIdentity.Pointer,
                    FileIdentityLength = fileIdentity.Length,
                };

                int result = CfExecuteAckDehydrate(ref operationInfo, ref parameters);
                ThrowIfFailed(result, nameof(CfExecute));
            }
            finally
            {
                fileIdentity.Dispose();
            }
        }

        public void DehydratePlaceholder(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            int openResult = CfOpenFileWithOplock(
                WindowsNativePath.ToWin32FilePath(filePath),
                CfOpenFileFlags.Exclusive,
                out IntPtr protectedHandle);
            ThrowIfFailed(openResult, nameof(CfOpenFileWithOplock));
            try
            {
                int dehydrateResult = CfDehydratePlaceholder(
                    protectedHandle,
                    0,
                    -1,
                    CfDehydrateFlags.None,
                    IntPtr.Zero);
                ThrowIfFailed(dehydrateResult, nameof(CfDehydratePlaceholder));
            }
            finally
            {
                CfCloseHandle(protectedHandle);
            }
        }


        private static void ThrowIfFailed(int hresult, string operation)
        {
            if (hresult < Succeeded)
            {
                throw new WindowsCloudFilesNativeException(operation, hresult);
            }
        }

        private static int HResultFromWin32(int error)
        {
            return error <= 0
                ? error
                : unchecked((int)(0x80070000u | (uint)error));
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            FileDesiredAccess dwDesiredAccess,
            FileShareMode dwShareMode,
            IntPtr lpSecurityAttributes,
            FileCreationDisposition dwCreationDisposition,
            FileFlagsAndAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile);

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

        [DllImport("CldApi.dll", ExactSpelling = true)]
        private static extern void CfCloseHandle(IntPtr FileHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct CfSyncRegistration
        {
            public uint StructSize;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string ProviderName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string ProviderVersion;

            public IntPtr SyncRootIdentity;

            public uint SyncRootIdentityLength;

            public IntPtr FileIdentity;

            public uint FileIdentityLength;

            public Guid ProviderId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CfSyncPolicies
        {
            public uint StructSize;

            public CfHydrationPolicy Hydration;

            public CfPopulationPolicy Population;

            public CfInSyncPolicy InSync;

            public CfHardLinkPolicy HardLink;

            public CfPlaceholderManagementPolicy PlaceholderManagement;

            public static CfSyncPolicies CreateDefault()
            {
                return new CfSyncPolicies
                {
                    StructSize = (uint)Marshal.SizeOf<CfSyncPolicies>(),
                    Hydration = new CfHydrationPolicy(
                        CfHydrationPolicyPrimary.Full,
                        (ushort)CfHydrationPolicyModifier.AutoDehydrationAllowed),
                    Population = new CfPopulationPolicy(CfPopulationPolicyPrimary.AlwaysFull, modifier: 0),
                    InSync = CfInSyncPolicy.TrackAll,
                    HardLink = CfHardLinkPolicy.None,
                    PlaceholderManagement = CfPlaceholderManagementPolicy.Default,
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CfHydrationPolicy
        {
            public CfHydrationPolicy(CfHydrationPolicyPrimary primary, ushort modifier)
            {
                Primary = primary;
                Modifier = modifier;
            }

            public readonly CfHydrationPolicyPrimary Primary;

            public readonly ushort Modifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CfPopulationPolicy
        {
            public CfPopulationPolicy(CfPopulationPolicyPrimary primary, ushort modifier)
            {
                Primary = primary;
                Modifier = modifier;
            }

            public readonly CfPopulationPolicyPrimary Primary;

            public readonly ushort Modifier;
        }

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

        [Flags]
        private enum CfRegisterFlags : uint
        {
            Update = 0x00000001,
            MarkInSyncOnRoot = 0x00000004,
        }

        private enum CfHydrationPolicyPrimary : ushort
        {
            Full = 2,
        }

        [Flags]
        private enum CfHydrationPolicyModifier : ushort
        {
            AutoDehydrationAllowed = 0x0004,
        }

        private enum CfPopulationPolicyPrimary : ushort
        {
            AlwaysFull = 3,
        }

        [Flags]
        private enum CfInSyncPolicy : uint
        {
            TrackAll = 0x00ffffff,
        }

        private enum CfHardLinkPolicy : uint
        {
            None = 0x00000000,
        }

        private enum CfPlaceholderManagementPolicy : uint
        {
            Default = 0x00000000,
        }

        [Flags]
        private enum CfCreateFlags : uint
        {
            StopOnError = 0x00000001,
        }

        [Flags]
        private enum CfPlaceholderCreateFlags : uint
        {
            MarkInSync = 0x00000002,
        }

        [Flags]
        private enum CfConvertFlags : uint
        {
            None = 0x00000000,
            MarkInSync = 0x00000001,
        }

        [Flags]
        private enum CfConnectFlags : uint
        {
            None = 0x00000000,
            RequireProcessInfo = 0x00000002,
            BlockSelfImplicitHydration = 0x00000008,
        }

        [Flags]
        private enum CfOpenFileFlags : uint
        {
            None = 0x00000000,
            Exclusive = 0x00000001,
            WriteAccess = 0x00000002,
        }

        [Flags]
        private enum FileDesiredAccess : uint
        {
            ReadData = 0x00000001,
            WriteData = 0x00000002,
            WriteAttributes = 0x00000100,
        }

        [Flags]
        private enum FileShareMode : uint
        {
            Read = 0x00000001,
            Write = 0x00000002,
            Delete = 0x00000004,
        }

        private enum FileCreationDisposition : uint
        {
            OpenExisting = 3,
        }

        [Flags]
        private enum FileFlagsAndAttributes : uint
        {
            OpenReparsePoint = 0x00200000,
            BackupSemantics = 0x02000000,
        }

        [Flags]
        private enum CfDehydrateFlags : uint
        {
            None = 0x00000000,
        }

        [Flags]
        private enum CfUpdateFlags : uint
        {
            MarkInSync = 0x00000002,
            Dehydrate = 0x00000004,
            AllowPartial = 0x00000400,
        }

        private enum CfPinState : int
        {
            Unspecified = 0,
            Pinned = 1,
            Unpinned = 2,
            Excluded = 3,
            Inherit = 4,
        }

        [Flags]
        private enum CfSetPinFlags : uint
        {
            None = 0x00000000,
        }

        private enum CfInSyncState : uint
        {
            NotInSync = 0,
            InSync = 1,
        }

        [Flags]
        private enum CfSetInSyncFlags : uint
        {
            None = 0x00000000,
        }

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

        private sealed class NativeCallbackState : IDisposable
        {
            private readonly WindowsCloudFilesCallbackDispatcher _dispatcher;
            private readonly GCHandle _contextHandle;
            private readonly CfCallback _fetchDataCallback;
            private readonly CfCallback _cancelFetchDataCallback;
            private readonly CfCallback _notifyDehydrateCallback;
            private readonly CfCallback _notifyDehydrateCompletionCallback;
            private int _disposed;

            public NativeCallbackState(
                IWindowsCloudFilesCallbackHandler handler,
                WindowsCloudFilesNativeApi owner)
            {
                ArgumentNullException.ThrowIfNull(owner);
                _dispatcher = new WindowsCloudFilesCallbackDispatcher(
                    handler,
                    owner.TransferData,
                    owner.AcknowledgeDehydrate);
                _fetchDataCallback = HandleFetchData;
                _cancelFetchDataCallback = HandleCancelFetchData;
                _notifyDehydrateCallback = HandleNotifyDehydrate;
                _notifyDehydrateCompletionCallback = HandleNotifyDehydrateCompletion;
                _contextHandle = GCHandle.Alloc(this);
                CallbackTable =
                [
                    new CfCallbackRegistration(
                        CfCallbackType.FetchData,
                        Marshal.GetFunctionPointerForDelegate(_fetchDataCallback)),
                    new CfCallbackRegistration(
                        CfCallbackType.CancelFetchData,
                        Marshal.GetFunctionPointerForDelegate(_cancelFetchDataCallback)),
                    new CfCallbackRegistration(
                        CfCallbackType.NotifyDehydrate,
                        Marshal.GetFunctionPointerForDelegate(_notifyDehydrateCallback)),
                    new CfCallbackRegistration(
                        CfCallbackType.NotifyDehydrateCompletion,
                        Marshal.GetFunctionPointerForDelegate(_notifyDehydrateCompletionCallback)),
                    new CfCallbackRegistration(CfCallbackType.None, IntPtr.Zero),
                ];
            }

            public CfCallbackRegistration[] CallbackTable { get; }

            public IntPtr Context => GCHandle.ToIntPtr(_contextHandle);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _dispatcher.Dispose();

                if (_contextHandle.IsAllocated)
                {
                    _contextHandle.Free();
                }
            }

            private void HandleFetchData(IntPtr callbackInfo, IntPtr callbackParameters)
            {
                if (_disposed != 0)
                {
                    return;
                }

                WindowsCloudFilesFetchDataRequest request;
                try
                {
                    CfCallbackInfo info = Marshal.PtrToStructure<CfCallbackInfo>(callbackInfo);
                    CfCallbackFetchDataParameters parameters =
                        Marshal.PtrToStructure<CfCallbackFetchDataParameters>(callbackParameters);
                    request = new WindowsCloudFilesFetchDataRequest(
                        new WindowsCloudFilesConnectionKey(info.ConnectionKey),
                        new WindowsCloudFilesTransferKey(info.TransferKey),
                        new WindowsCloudFilesRequestKey(info.RequestKey),
                        CopyBytes(info.FileIdentity, info.FileIdentityLength),
                        info.FileSize,
                        parameters.RequiredFileOffset,
                        parameters.RequiredLength,
                        parameters.OptionalFileOffset,
                        parameters.OptionalLength,
                        Marshal.PtrToStringUni(info.NormalizedPath),
                        info.PriorityHint,
                        TryReadProcessInfo(info.ProcessInfo));
                }
                catch
                {
                    return;
                }

                _dispatcher.QueueFetchData(request);
            }

            private static WindowsCloudFilesProcessInfo? TryReadProcessInfo(IntPtr processInfo)
            {
                if (processInfo == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    CfProcessInfo info = Marshal.PtrToStructure<CfProcessInfo>(processInfo);
                    return new WindowsCloudFilesProcessInfo(
                        info.ProcessId,
                        Marshal.PtrToStringUni(info.ImagePath),
                        Marshal.PtrToStringUni(info.PackageName),
                        Marshal.PtrToStringUni(info.ApplicationId),
                        Marshal.PtrToStringUni(info.CommandLine),
                        info.SessionId);
                }
                catch
                {
                    return null;
                }
            }

            private void HandleCancelFetchData(IntPtr callbackInfo, IntPtr callbackParameters)
            {
                if (_disposed != 0)
                {
                    return;
                }

                try
                {
                    CfCallbackInfo info = Marshal.PtrToStructure<CfCallbackInfo>(callbackInfo);
                    CfCallbackCancelFetchDataParameters parameters =
                        Marshal.PtrToStructure<CfCallbackCancelFetchDataParameters>(callbackParameters);
                    var request = new WindowsCloudFilesCancelFetchDataRequest(
                        new WindowsCloudFilesConnectionKey(info.ConnectionKey),
                        new WindowsCloudFilesTransferKey(info.TransferKey),
                        new WindowsCloudFilesRequestKey(info.RequestKey),
                        parameters.FileOffset,
                        parameters.Length);

                    _dispatcher.CancelFetchData(request);
                }
                catch
                {
                }
            }

            private void HandleNotifyDehydrate(IntPtr callbackInfo, IntPtr callbackParameters)
            {
                if (_disposed != 0)
                {
                    return;
                }

                WindowsCloudFilesDehydrateRequest request;
                try
                {
                    CfCallbackInfo info = Marshal.PtrToStructure<CfCallbackInfo>(callbackInfo);
                    CfCallbackDehydrateParameters parameters =
                        Marshal.PtrToStructure<CfCallbackDehydrateParameters>(callbackParameters);
                    request = new WindowsCloudFilesDehydrateRequest(
                        new WindowsCloudFilesConnectionKey(info.ConnectionKey),
                        new WindowsCloudFilesTransferKey(info.TransferKey),
                        new WindowsCloudFilesRequestKey(info.RequestKey),
                        CopyBytes(info.FileIdentity, info.FileIdentityLength),
                        Marshal.PtrToStringUni(info.NormalizedPath),
                        ToDehydrateReason(parameters.Reason),
                        (parameters.Flags & 0x00000001) != 0);
                }
                catch
                {
                    return;
                }

                _dispatcher.QueueDehydrate(request);
            }

            private void HandleNotifyDehydrateCompletion(IntPtr callbackInfo, IntPtr callbackParameters)
            {
                if (_disposed != 0)
                {
                    return;
                }

                try
                {
                    CfCallbackInfo info = Marshal.PtrToStructure<CfCallbackInfo>(callbackInfo);
                    CfCallbackDehydrateCompletionParameters parameters =
                        Marshal.PtrToStructure<CfCallbackDehydrateCompletionParameters>(callbackParameters);
                    var notification = new WindowsCloudFilesDehydrateCompletionNotification(
                        new WindowsCloudFilesConnectionKey(info.ConnectionKey),
                        new WindowsCloudFilesTransferKey(info.TransferKey),
                        new WindowsCloudFilesRequestKey(info.RequestKey),
                        CopyBytes(info.FileIdentity, info.FileIdentityLength),
                        Marshal.PtrToStringUni(info.NormalizedPath),
                        ToDehydrateReason(parameters.Reason),
                        (parameters.Flags & 0x00000001) != 0,
                        (parameters.Flags & 0x00000002) != 0);

                    _dispatcher.NotifyDehydrateCompleted(notification);
                }
                catch
                {
                }
            }

            private static WindowsCloudFilesDehydrateReason ToDehydrateReason(int reason)
            {
                return Enum.IsDefined(typeof(WindowsCloudFilesDehydrateReason), reason)
                    ? (WindowsCloudFilesDehydrateReason)reason
                    : WindowsCloudFilesDehydrateReason.Never;
            }

            private static byte[] CopyBytes(IntPtr source, uint length)
            {
                if (source == IntPtr.Zero || length == 0)
                {
                    return [];
                }

                if (length > int.MaxValue)
                {
                    throw new InvalidOperationException("Cloud Files callback identity is too large.");
                }

                byte[] bytes = new byte[(int)length];
                Marshal.Copy(source, bytes, 0, bytes.Length);
                return bytes;
            }
        }

        private readonly struct PinnedBuffer : IDisposable
        {
            private readonly GCHandle _handle;

            private PinnedBuffer(byte[]? buffer)
            {
                if (buffer is not { Length: > 0 })
                {
                    Pointer = IntPtr.Zero;
                    Length = 0;
                    _handle = default;
                    return;
                }

                _handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                Pointer = _handle.AddrOfPinnedObject();
                Length = (uint)buffer.Length;
            }

            public IntPtr Pointer { get; }

            public uint Length { get; }

            public static PinnedBuffer Pin(byte[]? buffer)
            {
                return new PinnedBuffer(buffer);
            }

            public void Dispose()
            {
                if (_handle.IsAllocated)
                {
                    _handle.Free();
                }
            }
        }
    }
}
