// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsStorageProviderSyncRootRegistrar
    {
        bool IsSupported();

        bool IsRegistered(Guid syncPairId);

        void Register(WindowsStorageProviderSyncRootRegistration registration);

        void Unregister(Guid syncPairId);

        void UnregisterAllForCurrentUser();
    }
}
