// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Shell
{
    [SupportedOSPlatform("windows")]
    internal class WindowsTaskbarStatusOverlay : IDisposable
    {
        private const uint LoadFromFile = 0x00000010;
        private const uint LoadTransparent = 0x00000020;
        private const uint ImageIcon = 1;
        private const uint InProcessServerContext = 0x1;
        private const int SuccessHResult = 0;
        private static readonly Guid TaskbarListClassId = new("56FDF344-FD6D-11D0-958A-006097C9A090");
        private static readonly Guid TaskbarListInterfaceId = new("C43DC798-95D1-4BEA-9030-BB99E2983A1A");

        private readonly Window _window;
        private DesktopTrayStatusKind _currentKind = DesktopTrayStatusKind.Unknown;
        private IWindowsTaskbarList4? _taskbar;
        private bool _disposed;

        public WindowsTaskbarStatusOverlay(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _window.Opened += OnWindowOpened;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _window.Opened -= OnWindowOpened;
            ApplyOverlay(DesktopTrayStatusKind.Idle);
            if (_taskbar is not null)
            {
                Marshal.FinalReleaseComObject(_taskbar);
                _taskbar = null;
            }

            _disposed = true;
        }

        public void Update(DesktopTrayStatusKind kind)
        {
            if (_disposed || _currentKind == kind)
            {
                return;
            }

            _currentKind = kind;
            ApplyOverlay(kind);
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            ApplyOverlay(_currentKind);
        }

        private void ApplyOverlay(DesktopTrayStatusKind kind)
        {
            IPlatformHandle? platformHandle = _window.TryGetPlatformHandle();
            if (platformHandle is null
                || !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IWindowsTaskbarList4 taskbar = GetTaskbar();
            string? iconPath = DesktopTaskbarOverlayIconAssetResolver.Resolve(kind);
            nint iconHandle = iconPath is null ? nint.Zero : LoadOverlayIcon(iconPath);
            try
            {
                int result = taskbar.SetOverlayIcon(
                    platformHandle.Handle,
                    iconHandle,
                    ResolveDescription(kind));
                if (result != SuccessHResult)
                {
                    Trace.TraceWarning(
                        "Cotton Sync taskbar overlay update failed with HRESULT 0x{0:X8}.",
                        result);
                }
                else
                {
                    Trace.TraceInformation(
                        "Cotton Sync taskbar overlay updated: status={0}, visible={1}.",
                        kind,
                        iconHandle != nint.Zero);
                }
            }
            finally
            {
                if (iconHandle != nint.Zero)
                {
                    DestroyIcon(iconHandle);
                }
            }
        }

        private IWindowsTaskbarList4 GetTaskbar()
        {
            if (_taskbar is not null)
            {
                return _taskbar;
            }

            Guid classId = TaskbarListClassId;
            Guid interfaceId = TaskbarListInterfaceId;
            int createResult = CoCreateInstance(
                ref classId,
                nint.Zero,
                InProcessServerContext,
                ref interfaceId,
                out IWindowsTaskbarList4? taskbar);
            Marshal.ThrowExceptionForHR(createResult);
            _taskbar = taskbar
                ?? throw new InvalidOperationException("Windows taskbar integration could not be created.");
            int result = _taskbar.HrInit();
            if (result != SuccessHResult)
            {
                Marshal.FinalReleaseComObject(_taskbar);
                _taskbar = null;
                throw new COMException("Windows taskbar integration could not be initialized.", result);
            }

            return _taskbar;
        }

        private static nint LoadOverlayIcon(string iconPath)
        {
            nint iconHandle = LoadImage(
                nint.Zero,
                iconPath,
                ImageIcon,
                0,
                0,
                LoadFromFile | LoadTransparent);
            if (iconHandle == nint.Zero)
            {
                throw new InvalidOperationException("Taskbar overlay icon could not be loaded: " + iconPath);
            }

            return iconHandle;
        }

        private static string ResolveDescription(DesktopTrayStatusKind kind)
        {
            return kind switch
            {
                DesktopTrayStatusKind.Unknown => string.Empty,
                DesktopTrayStatusKind.SignedOut => "Signed out",
                DesktopTrayStatusKind.Idle => "Connected",
                DesktopTrayStatusKind.Syncing => "Syncing",
                DesktopTrayStatusKind.Paused => "Paused",
                DesktopTrayStatusKind.Offline => "Offline",
                DesktopTrayStatusKind.Error => "Action required",
                DesktopTrayStatusKind.Uploading => "Uploading",
                DesktopTrayStatusKind.Downloading => "Downloading",
                DesktopTrayStatusKind.FreeingSpace => "Freeing space",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "Unsupported taskbar status cannot be described."),
            };
        }

        [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint LoadImage(
            nint instanceHandle,
            string name,
            uint type,
            int desiredWidth,
            int desiredHeight,
            uint loadFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(nint iconHandle);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            nint outerUnknown,
            uint context,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IWindowsTaskbarList4? taskbar);
    }
}
