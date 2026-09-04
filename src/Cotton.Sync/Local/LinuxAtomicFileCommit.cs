// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cotton.Sync.Local
{
    internal static class LinuxAtomicFileCommit
    {
        private const int CurrentWorkingDirectory = -100;
        private const uint RenameNoReplace = 1;
        private const uint RenameExchange = 2;

        public static void MoveNoReplace(string sourcePath, string destinationPath)
        {
            Rename(sourcePath, destinationPath, RenameNoReplace);
        }

        public static void Exchange(string sourcePath, string destinationPath)
        {
            Rename(sourcePath, destinationPath, RenameExchange);
        }

        private static void Rename(string sourcePath, string destinationPath, uint flags)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Atomic Linux file commits require Linux.");
            }

            int result;
            try
            {
                result = RenameAt2(CurrentWorkingDirectory, sourcePath, CurrentWorkingDirectory, destinationPath, flags);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("Atomic Linux file commits require libc renameat2 support.", exception);
            }

            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                Win32Exception nativeError = new(error);
                throw new IOException(
                    "Could not atomically commit local file from '" + sourcePath + "' to '" + destinationPath + "': " + nativeError.Message,
                    nativeError);
            }
        }

        [DllImport("libc", EntryPoint = "renameat2", ExactSpelling = true, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RenameAt2(
            int oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            int newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
            uint flags);
    }
}
