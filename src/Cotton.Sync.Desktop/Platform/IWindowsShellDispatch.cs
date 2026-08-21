// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    [ComImport]
    [Guid("286E6F1B-7113-4355-9562-96B7E9D64C54")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IWindowsShellDispatch
    {
        [DispId(1610743810)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IWindowsShellFolder? NameSpace([In, MarshalAs(UnmanagedType.Struct)] object directory);
    }
}
