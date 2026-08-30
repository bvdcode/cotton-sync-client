// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        private class RecordingShellChangeNotifier : IWindowsShellChangeNotifier
        {
            public List<string> ItemUpdates { get; } = [];

            public List<string> DirectoryUpdates { get; } = [];

            public void NotifyItemUpdated(string path)
            {
                ItemUpdates.Add(path);
            }

            public void NotifyDirectoryUpdated(string path)
            {
                DirectoryUpdates.Add(path);
            }
        }

        private class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            private readonly List<string> _operationLog;

            public FakeStorageProviderSyncRootRegistrar(List<string> operationLog)
            {
                _operationLog = operationLog;
            }

            public List<WindowsStorageProviderSyncRootRegistration> Registrations { get; } = [];

            public List<Guid> UnregisteredSyncPairIds { get; } = [];

            public List<string> UnregisteredLocalRootPaths { get; } = [];

            public int UnregisterAllCalls { get; private set; }

            public bool KeepRegistrationAfterUnregister { get; set; }

            public bool IsSupported()
            {
                return true;
            }

            public bool IsRegistered(Guid syncPairId)
            {
                return Registrations.Any(registration => registration.SyncPairId == syncPairId);
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                _operationLog.Add("storage-provider-register");
                Registrations.Add(registration);
            }

            public void Unregister(Guid syncPairId, string localRootPath)
            {
                _operationLog.Add("storage-provider-unregister");
                UnregisteredSyncPairIds.Add(syncPairId);
                UnregisteredLocalRootPaths.Add(localRootPath);
                if (!KeepRegistrationAfterUnregister)
                {
                    Registrations.RemoveAll(registration => registration.SyncPairId == syncPairId);
                }
            }

            public void UnregisterAllForCurrentUser()
            {
                _operationLog.Add("storage-provider-unregister-all");
                UnregisterAllCalls++;
            }
        }

        private class RecordingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }
    }
}
