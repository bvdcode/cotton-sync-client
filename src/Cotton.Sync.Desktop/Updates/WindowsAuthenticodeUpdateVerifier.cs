// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Updates
{
    internal class WindowsAuthenticodeUpdateVerifier : IDesktopUpdateAuthenticodeVerifier
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeWholeChain = 1;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public void VerifyTrustedInstaller(string installerPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Desktop update installer trust verification requires Windows.");
            }

            int result = VerifyEmbeddedSignature(installerPath);
            if (result != 0)
            {
                throw new InvalidDataException(
                    "Cotton Sync update installer is not signed by a trusted publisher. WinVerifyTrust result: "
                    + FormatResult(result)
                    + ".");
            }
        }

        private static int VerifyEmbeddedSignature(string installerPath)
        {
            WinTrustFileInfo fileInfo = new()
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = installerPath,
                File = IntPtr.Zero,
                KnownSubject = IntPtr.Zero,
            };
            WinTrustData trustData = new()
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                StateAction = WtdStateActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProvFlags = 0,
                UiContext = 0,
                SignatureSettings = IntPtr.Zero,
            };
            IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            IntPtr trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                trustData.FileInfo = fileInfoPointer;
                Marshal.StructureToPtr(trustData, trustDataPointer, false);

                int result = WinVerifyTrust(
                    IntPtr.Zero,
                    WinTrustActionGenericVerifyV2,
                    trustDataPointer);

                WinTrustData verifiedTrustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                verifiedTrustData.StateAction = WtdStateActionClose;
                Marshal.StructureToPtr(verifiedTrustData, trustDataPointer, false);
                _ = WinVerifyTrust(
                    IntPtr.Zero,
                    WinTrustActionGenericVerifyV2,
                    trustDataPointer);

                return result;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.DestroyStructure<WinTrustData>(trustDataPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeHGlobal(trustDataPointer);
            }
        }

        private static string FormatResult(int result)
        {
            return "0x" + unchecked((uint)result).ToString("X8", CultureInfo.InvariantCulture);
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint CbStruct;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string FilePath;

            public IntPtr File;

            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint CbStruct;

            public IntPtr PolicyCallbackData;

            public IntPtr SipClientData;

            public uint UiChoice;

            public uint RevocationChecks;

            public uint UnionChoice;

            public IntPtr FileInfo;

            public uint StateAction;

            public IntPtr StateData;

            public IntPtr UrlReference;

            public uint ProvFlags;

            public uint UiContext;

            public IntPtr SignatureSettings;
        }
    }
}
