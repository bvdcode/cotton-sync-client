// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    [ComImport]
    [Guid("EDC817AA-92B8-11D1-B075-00C04FC33AA5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IWindowsShellFolderItem
    {
        [DispId(1610743823)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IWindowsShellFolderItemVerbs Verbs();

        [DispId(1610809345)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object? ExtendedProperty([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }
}
