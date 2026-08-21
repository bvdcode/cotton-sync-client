// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    [ComImport]
    [Guid("A7AE5F64-C4D7-4D7F-9307-4D24EE54B841")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IWindowsShellFolder
    {
        [DispId(1610743813)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IWindowsShellFolderItem? ParseName([In, MarshalAs(UnmanagedType.BStr)] string name);
    }
}
