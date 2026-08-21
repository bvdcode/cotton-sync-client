// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        private class NativeCallbackState : IDisposable
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
                    WindowsCloudFilesCancelFetchDataRequest request = new(
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
                    WindowsCloudFilesDehydrateCompletionNotification notification = new(
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
    }
}
