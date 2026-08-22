// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Composition
{
    internal record DesktopCompositionSnapshot(
        Type AsyncResourceType,
        Type LocalChangeCoordinatorType,
        Type RemoteChangeCoordinatorType,
        Type PeriodicSyncCoordinatorType,
        Type PairWorkType,
        Type RemoteChangePairWorkType,
        Type FilePlaceholderRepairType,
        Type DirectoryPlaceholderRepairType,
        Type UploadFinalizationType,
        Type SyncEnginePairWorkType,
        Type PlaceholderWriterType,
        Type SyncCoreLifecycleType,
        Type SyncPairDeletionHandlerType);
}
