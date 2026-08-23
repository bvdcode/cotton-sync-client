// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    [ComImport]
    [Guid("C43DC798-95D1-4BEA-9030-BB99E2983A1A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWindowsTaskbarList4
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint windowHandle);

        [PreserveSig]
        int DeleteTab(nint windowHandle);

        [PreserveSig]
        int ActivateTab(nint windowHandle);

        [PreserveSig]
        int SetActiveAlt(nint windowHandle);

        [PreserveSig]
        int MarkFullscreenWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool isFullscreen);

        [PreserveSig]
        int SetProgressValue(nint windowHandle, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(nint windowHandle, uint flags);

        [PreserveSig]
        int RegisterTab(nint tabHandle, nint ownerHandle);

        [PreserveSig]
        int UnregisterTab(nint tabHandle);

        [PreserveSig]
        int SetTabOrder(nint tabHandle, nint insertBeforeHandle);

        [PreserveSig]
        int SetTabActive(nint tabHandle, nint ownerHandle, uint reserved);

        [PreserveSig]
        int ThumbBarAddButtons(nint windowHandle, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarUpdateButtons(nint windowHandle, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarSetImageList(nint windowHandle, nint imageListHandle);

        [PreserveSig]
        int SetOverlayIcon(
            nint windowHandle,
            nint iconHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string description);

        [PreserveSig]
        int SetThumbnailTooltip(
            nint windowHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string tooltip);

        [PreserveSig]
        int SetThumbnailClip(nint windowHandle, nint rectangle);

        [PreserveSig]
        int SetTabProperties(nint tabHandle, uint flags);
    }
}
