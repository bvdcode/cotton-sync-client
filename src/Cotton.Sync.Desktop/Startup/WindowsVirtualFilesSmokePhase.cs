// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal enum WindowsVirtualFilesSmokePhase
    {
        Default,
        LeaveRegistered,
        ReconnectExisting,
        InitialStreamingLogging,
        SteadyStateRepeat,
        LargeTree,
        NonEmptyPreservation,
        LargeHydrationProgress,
        RemovePairCleanup,
        LargeRemovePairCleanup,
        TrayQuitDisconnect,
        ExplorerFreeUpSpace,
        ExplorerAlwaysKeep,
        ExplorerAlwaysKeepMissingPlaceholder,
        ExplorerAlwaysKeepDuringPopulation,
        RemoteUpdateAfterDehydrate,
        ReplaceCloudOnlyUpload,
        ExcelAtomicSave,
        ProviderMetadataUserEdit,
        LocalRenameAfterProviderWrite,
        LocalMoveAfterProviderWrite,
        ShellShareLinkTargets,
        DesktopRootLifecycle,
        DesktopSessionRestore,
    }
}
