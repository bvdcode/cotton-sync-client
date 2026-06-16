// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

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
