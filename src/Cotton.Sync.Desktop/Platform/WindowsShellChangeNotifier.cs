// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsShellChangeNotifier : IWindowsShellChangeNotifier
    {
        private const int ShcneAttributes = 0x00000800;
        private const int ShcneUpdatedir = 0x00001000;
        private const int ShcneUpdateitem = 0x00002000;
        private const uint ShcnfPathw = 0x0005;
        private const uint ShcnfFlushnowait = 0x2000;

        public void NotifyItemUpdated(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string fullPath = Path.GetFullPath(path);
            Notify(ShcneAttributes, fullPath);
            Notify(ShcneUpdateitem, fullPath);
        }

        public void NotifyDirectoryUpdated(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string fullPath = Path.GetFullPath(path);
            Notify(ShcneAttributes, fullPath);
            Notify(ShcneUpdateitem, fullPath);
            Notify(ShcneUpdatedir, fullPath);
        }

        private static void Notify(int eventId, string path)
        {
            SHChangeNotify(eventId, ShcnfPathw | ShcnfFlushnowait, path, null);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(
            int wEventId,
            uint uFlags,
            string? dwItem1,
            string? dwItem2);
    }
}
