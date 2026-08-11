// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Shell
{
    internal class DesktopShellControllerOptions
    {
        public DesktopStartupOptions StartupOptions { get; init; } = DesktopStartupOptions.Empty;

        public Func<DesktopTokenStorageCapabilitySnapshot>? TokenStorageCapabilities { get; init; }

        public Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>>? TokenStorageVerifier { get; init; }

        public TimeSpan? SavedSessionRestoreTimeout { get; init; }

        public TimeSpan? SavedSessionRestoreRetryBaseDelay { get; init; }

        public TimeSpan? ServerProbeTimeout { get; init; }

        public TimeSpan? TokenStorageVerificationTimeout { get; init; }

        public IDesktopUpdateService? UpdateService { get; init; }

        public IDesktopUpdateInstaller? UpdateInstaller { get; init; }
    }
}
