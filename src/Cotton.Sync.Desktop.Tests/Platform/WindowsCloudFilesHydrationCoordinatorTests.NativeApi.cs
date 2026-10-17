// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.Progress;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesHydrationCoordinatorTests
    {
        internal class RecordingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
            }
        }

        private class RecordingObserver<T> : IObserver<T>
        {
            public List<T> Values { get; } = [];

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
                throw error;
            }

            public void OnNext(T value)
            {
                Values.Add(value);
            }
        }

        internal class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public ConcurrentQueue<WindowsCloudFilesProviderProgress> ProviderProgress { get; } = new();

            public Action<WindowsCloudFilesProviderProgress>? OnProviderProgress { get; set; }

            public void ReportProviderProgress(WindowsCloudFilesProviderProgress progress)
            {
                ProviderProgress.Enqueue(progress);
                OnProviderProgress?.Invoke(progress);
            }

            public List<WindowsCloudFilesAckDehydrateData> Dehydrates { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public WindowsCloudFilesPlaceholderState InSyncStateAfterSet { get; set; } =
                WindowsCloudFilesPlaceholderState.Placeholder | WindowsCloudFilesPlaceholderState.InSync;

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
            {
            }

            public void SetInSyncState(string filePath, bool inSync = true)
            {
                if (inSync)
                {
                    InSyncPaths.Add(filePath);
                }
                else
                {
                    InSyncPaths.Remove(filePath);
                }
            }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath)
            {
                return InSyncPaths.Contains(filePath, StringComparer.OrdinalIgnoreCase)
                    ? InSyncStateAfterSet
                    : WindowsCloudFilesPlaceholderState.None;
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                return new WindowsCloudFilesConnection(
                    request.LocalRootPath,
                    new WindowsCloudFilesConnectionKey(1),
                    DisconnectSyncRoot);
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer with { Buffer = transfer.Buffer.ToArray() });
            }

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                Dehydrates.Add(dehydrate with { FileIdentity = dehydrate.FileIdentity.ToArray() });
            }

            public void DehydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }

            public void HydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }
        }
    }
}
