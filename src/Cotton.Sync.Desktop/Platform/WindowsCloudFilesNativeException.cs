// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Globalization;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesNativeException : Exception
    {
        public WindowsCloudFilesNativeException(string operation, int hresult)
            : base(CreateMessage(operation, hresult))
        {
            Operation = operation;
            HResult = hresult;
        }

        public string Operation { get; }

        private static string CreateMessage(string operation, int hresult)
        {
            return operation + " failed with HRESULT 0x" + hresult.ToString("X8", CultureInfo.InvariantCulture) + ".";
        }
    }
}
