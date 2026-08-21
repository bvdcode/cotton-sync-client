// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    [ComImport]
    [Guid("1F8352C0-50B0-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IWindowsShellFolderItemVerbs
    {
        [DispId(1610743808)]
        int Count { get; }

        [DispId(1610743811)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IWindowsShellFolderItemVerb? Item([In, MarshalAs(UnmanagedType.Struct)] object index);
    }
}
