// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal static class WindowsVirtualFilesSmokePhaseCatalog
    {
        private static readonly IReadOnlyDictionary<string, WindowsVirtualFilesSmokePhase> Phases =
            new Dictionary<string, WindowsVirtualFilesSmokePhase>(StringComparer.Ordinal)
            {
                ["leave-registered"] = WindowsVirtualFilesSmokePhase.LeaveRegistered,
                ["reconnect-existing"] = WindowsVirtualFilesSmokePhase.ReconnectExisting,
                ["initial-streaming-logging"] = WindowsVirtualFilesSmokePhase.InitialStreamingLogging,
                ["steady-state-repeat"] = WindowsVirtualFilesSmokePhase.SteadyStateRepeat,
                ["large-tree"] = WindowsVirtualFilesSmokePhase.LargeTree,
                ["non-empty-preservation"] = WindowsVirtualFilesSmokePhase.NonEmptyPreservation,
                ["large-hydration-progress"] = WindowsVirtualFilesSmokePhase.LargeHydrationProgress,
                ["remove-pair-cleanup"] = WindowsVirtualFilesSmokePhase.RemovePairCleanup,
                ["large-remove-pair-cleanup"] = WindowsVirtualFilesSmokePhase.LargeRemovePairCleanup,
                ["tray-quit-disconnect"] = WindowsVirtualFilesSmokePhase.TrayQuitDisconnect,
                ["explorer-free-up-space"] = WindowsVirtualFilesSmokePhase.ExplorerFreeUpSpace,
                ["explorer-always-keep"] = WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeep,
                ["explorer-always-keep-missing-placeholder"] =
                    WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder,
                ["explorer-always-keep-during-population"] =
                    WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepDuringPopulation,
                ["remote-update-after-dehydrate"] = WindowsVirtualFilesSmokePhase.RemoteUpdateAfterDehydrate,
                ["replace-cloud-only-upload"] = WindowsVirtualFilesSmokePhase.ReplaceCloudOnlyUpload,
                ["excel-atomic-save"] = WindowsVirtualFilesSmokePhase.ExcelAtomicSave,
                ["provider-metadata-user-edit"] = WindowsVirtualFilesSmokePhase.ProviderMetadataUserEdit,
                ["local-rename-after-provider-write"] = WindowsVirtualFilesSmokePhase.LocalRenameAfterProviderWrite,
                ["local-move-after-provider-write"] = WindowsVirtualFilesSmokePhase.LocalMoveAfterProviderWrite,
                ["shell-share-link-targets"] = WindowsVirtualFilesSmokePhase.ShellShareLinkTargets,
                ["desktop-root-lifecycle"] = WindowsVirtualFilesSmokePhase.DesktopRootLifecycle,
                ["desktop-session-restore"] = WindowsVirtualFilesSmokePhase.DesktopSessionRestore,
            };

        private static readonly IReadOnlySet<WindowsVirtualFilesSmokePhase> ExplorerAvailabilityPhases =
            new HashSet<WindowsVirtualFilesSmokePhase>
            {
                WindowsVirtualFilesSmokePhase.ExplorerFreeUpSpace,
                WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeep,
                WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder,
                WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepDuringPopulation,
            };

        public static bool TryParse(string? value, out WindowsVirtualFilesSmokePhase phase)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                phase = WindowsVirtualFilesSmokePhase.Default;
                return true;
            }

            return Phases.TryGetValue(normalized, out phase);
        }

        public static bool RequiresExplorerAvailabilityVerbs(WindowsVirtualFilesSmokePhase phase)
        {
            return ExplorerAvailabilityPhases.Contains(phase);
        }
    }
}
