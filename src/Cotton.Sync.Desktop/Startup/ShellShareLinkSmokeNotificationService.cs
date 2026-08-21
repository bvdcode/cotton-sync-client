// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Startup
{
    internal class ShellShareLinkSmokeNotificationService : IDesktopNotificationService
    {
        public bool IsSupported => true;

        public string? LastMessage { get; private set; }

        public void Show(string title, string message)
        {
            LastMessage = message;
        }
    }
}
