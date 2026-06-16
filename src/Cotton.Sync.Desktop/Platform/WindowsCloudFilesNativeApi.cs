// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;
using System.Collections.Concurrent;

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
                    registration.LocalRootPath,
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

        public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
        {
            ArgumentNullException.ThrowIfNull(placeholder);
            Directory.CreateDirectory(placeholder.BaseDirectoryPath);

            PinnedBuffer fileIdentity = PinnedBuffer.Pin(placeholder.FileIdentity);
            try
            {
                var placeholders = new[]
                {
                    new CfPlaceholderCreateInfo
                    {
                        RelativeFileName = placeholder.RelativeFileName,
                        FsMetadata = CfFsMetadata.CreateFile(
                            placeholder.FileSizeBytes,
                            placeholder.CreatedAtUtc,
                            placeholder.UpdatedAtUtc),
                        FileIdentity = fileIdentity.Pointer,
                        FileIdentityLength = fileIdentity.Length,
                        Flags = CfPlaceholderCreateFlags.MarkInSync,
                        Result = Succeeded,
                        CreateUsn = 0,
                    },
                };

                int result = CfCreatePlaceholders(
                    placeholder.BaseDirectoryPath,
                    placeholders,
                    (uint)placeholders.Length,
                    CfCreateFlags.StopOnError,
                    out uint entriesProcessed);
                ThrowIfFailed(result, nameof(CfCreatePlaceholders));

                if (entriesProcessed != placeholders.Length)
                {
                    throw new WindowsCloudFilesNativeException(nameof(CfCreatePlaceholders), unchecked((int)0x80004005));
                }

                ThrowIfFailed(placeholders[0].Result, nameof(CfCreatePlaceholders));
            }
            finally
            {
                fileIdentity.Dispose();
            }
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
                CfConnectFlags.None,
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

        private static void ThrowIfFailed(int hresult, string operation)
        {
            if (hresult < Succeeded)
            {
                throw new WindowsCloudFilesNativeException(operation, hresult);
            }
        }

        [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CfRegisterSyncRoot(
            string SyncRootPath,
            ref CfSyncRegistration Registration,
            ref CfSyncPolicies Policies,
            CfRegisterFlags RegisterFlags);

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
        private static extern int CfExecute(
            ref CfOperationInfo OpInfo,
            ref CfOperationTransferDataParameters OpParams);

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
                    Hydration = new CfHydrationPolicy(CfHydrationPolicyPrimary.Full, modifier: 0),
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
        private enum CfConnectFlags : uint
        {
            None = 0x00000000,
        }

        private enum CfCallbackType : uint
        {
            FetchData = 0,
            CancelFetchData = 2,
            None = 0xffffffff,
        }

        private enum CfOperationType : uint
        {
            TransferData = 0,
        }

        [Flags]
        private enum CfOperationTransferDataFlags : uint
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

        private sealed class NativeCallbackState : IDisposable
        {
            private readonly IWindowsCloudFilesCallbackHandler _handler;
            private readonly WindowsCloudFilesNativeApi _owner;
            private readonly ConcurrentDictionary<long, CancellationTokenSource> _pendingFetches = new();
            private readonly GCHandle _contextHandle;
            private readonly CfCallback _fetchDataCallback;
            private readonly CfCallback _cancelFetchDataCallback;
            private int _disposed;

            public NativeCallbackState(
                IWindowsCloudFilesCallbackHandler handler,
                WindowsCloudFilesNativeApi owner)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _fetchDataCallback = HandleFetchData;
                _cancelFetchDataCallback = HandleCancelFetchData;
                _contextHandle = GCHandle.Alloc(this);
                CallbackTable =
                [
                    new CfCallbackRegistration(
                        CfCallbackType.FetchData,
                        Marshal.GetFunctionPointerForDelegate(_fetchDataCallback)),
                    new CfCallbackRegistration(
                        CfCallbackType.CancelFetchData,
                        Marshal.GetFunctionPointerForDelegate(_cancelFetchDataCallback)),
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

                foreach (CancellationTokenSource pending in _pendingFetches.Values)
                {
                    pending.Cancel();
                }

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
                        info.PriorityHint);
                }
                catch
                {
                    return;
                }

                var cancellation = new CancellationTokenSource();
                _pendingFetches[request.RequestKey.Value] = cancellation;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handler.HandleFetchDataAsync(request, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        TryTransferFailure(request);
                    }
                    finally
                    {
                        if (_pendingFetches.TryRemove(request.RequestKey.Value, out CancellationTokenSource? pending))
                        {
                            pending.Dispose();
                        }
                    }
                });
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

                    if (_pendingFetches.TryGetValue(request.RequestKey.Value, out CancellationTokenSource? pending))
                    {
                        pending.Cancel();
                    }

                    _handler.CancelFetchData(request);
                }
                catch
                {
                }
            }

            private void TryTransferFailure(WindowsCloudFilesFetchDataRequest request)
            {
                try
                {
                    _owner.TransferData(WindowsCloudFilesTransferData.Failure(request));
                }
                catch
                {
                }
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
