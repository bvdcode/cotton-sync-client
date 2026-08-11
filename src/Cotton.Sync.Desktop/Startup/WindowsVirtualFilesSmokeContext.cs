// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal record WindowsVirtualFilesSmokeContext(
        DesktopAppPaths Paths,
        DesktopStartupOptions StartupOptions,
        TextWriter Output,
        IWindowsCloudFilesAdapter CloudFiles,
        IWindowsCloudFilesNativeApi? NativeApi,
        SyncPairSettings SyncPair,
        WindowsCloudFilesDiagnostics Diagnostics,
        Func<string, CancellationToken, Task<string>> ReadAllTextAsync,
        WindowsVirtualFilesSmokePhase Phase,
        CancellationToken CancellationToken);
}
