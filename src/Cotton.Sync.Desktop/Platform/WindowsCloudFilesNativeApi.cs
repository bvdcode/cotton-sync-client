// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi : IWindowsCloudFilesNativeApi
    {
        private const int Succeeded = 0;
        private const int IntegrityHashBufferSize = 128 * 1024;
        private static readonly int PlaceholderBasicInfoIdentityOffset =
            Marshal.OffsetOf<CfPlaceholderBasicInfo>(nameof(CfPlaceholderBasicInfo.FileIdentity)).ToInt32();
        private static readonly int PlaceholderBasicInfoIdentityLengthOffset =
            Marshal.OffsetOf<CfPlaceholderBasicInfo>(nameof(CfPlaceholderBasicInfo.FileIdentityLength)).ToInt32();





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
            DisableOnDemandPopulation = 0x00000001,
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

        private enum CfPlaceholderInfoClass
        {
            Basic = 0,
        }

        [Flags]
        private enum FileDesiredAccess : uint
        {
            ReadData = 0x00000001,
            WriteData = 0x00000002,
            ReadAttributes = 0x00000080,
            WriteAttributes = 0x00000100,
        }

        private enum FileInfoByHandleClass
        {
            FileAttributeTagInfo = 9,
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
        private enum CfHydrateFlags : uint
        {
            None = 0x00000000,
        }

        [Flags]
        private enum CfUpdateFlags : uint
        {
            VerifyInSync = 0x00000001,
            MarkInSync = 0x00000002,
            Dehydrate = 0x00000004,
            DisableOnDemandPopulation = 0x00000010,
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

        private static CfPlaceholderCreateFlags CreatePlaceholderCreateFlags(bool isDirectory)
        {
            return (CfPlaceholderCreateFlags)WindowsCloudFilesPlaceholderFlags
                .CreatePlaceholderCreateFlags(isDirectory);
        }

        private static CfUpdateFlags CreateUpdateFlags(bool isDirectory)
        {
            return (CfUpdateFlags)WindowsCloudFilesPlaceholderFlags.CreateUpdateFlags(isDirectory);
        }

        [Flags]
        private enum CfSetInSyncFlags : uint
        {
            None = 0x00000000,
        }



    }
}
