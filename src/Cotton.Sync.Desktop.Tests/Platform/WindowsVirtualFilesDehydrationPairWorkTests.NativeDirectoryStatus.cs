// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [Platform(Include = "Win")]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task RunOnceAsync_UnpinnedDirectoryCompletesWithoutChangingChildStatus(
            bool startupRecovery,
            bool pendingChild)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-directory-status-" + Guid.NewGuid().ToString("N"));
            SyncPairSettings pair = CreateVirtualFilesPair();
            pair.Id = Guid.NewGuid();
            pair.LocalRootPath = root;
            string directoryPath = Path.Combine(root, "Reports");
            string childPath = Path.Combine(directoryPath, "report.txt");
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(WindowsCloudFilesNativeApiIntegrationTests.GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            FakeSyncStateStore stateStore = new();
            SyncStateEntry directoryState = CreateDirectoryState(pair, "Reports");
            stateStore.UpsertEntry(directoryState);
            Directory.CreateDirectory(root);
            try
            {
                using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(pair, new NoopWindowsCloudFilesCallbackHandler());
                adapter.CreateDirectoryPlaceholder(WindowsCloudFilesDirectoryRequestFactory.Create(pair, directoryState, "Reports"));
                byte[] directoryIdentity = nativeApi.GetPlaceholderIdentity(directoryPath);
                if (pendingChild)
                {
                    await File.WriteAllTextAsync(childPath, "Unsynced local changes must remain pending.");
                    nativeApi.ConvertToPlaceholder(childPath, [1, 2, 3], isDirectory: false, markInSync: false);
                }

                nativeApi.SetPinState(directoryPath, WindowsCloudFilesPinState.Unpinned);
                nativeApi.SetInSyncState(directoryPath, inSync: false);
                Assert.That(nativeApi.GetPlaceholderState(directoryPath).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.False);
                RecordingSyncPairWork inner = new();
                WindowsVirtualFilesDehydrationPairWork work = new(inner, stateStore, adapter, new FakeCloudFilesAdapter());
                SyncRunRequest request = startupRecovery
                    ? SyncRunRequest.ForFull(SyncRunCause.Periodic)
                    : SyncRunRequest.ForLocalChangedPaths(["Reports"]);

                await work.RunOnceAsync(pair, request);

                if (!pendingChild)
                {
                    string shellState = await ReadStorageProviderStateAsync(directoryPath);
                    Assert.That(shellState, Is.Not.Empty.And.Not.EqualTo("4"),
                        "Explorer must not report sync pending after the directory availability request completes.");
                }

                Assert.Multiple(() =>
                {
                    Assert.That(nativeApi.GetPlaceholderState(directoryPath).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(nativeApi.GetPlaceholderIdentity(directoryPath), Is.EqualTo(directoryIdentity));
                    Assert.That(File.GetAttributes(directoryPath).HasFlag((FileAttributes)FileAttributeUnpinned), Is.True);
                    Assert.That(inner.Requests.Count, Is.EqualTo(startupRecovery ? 1 : 0));
                });
                if (pendingChild)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(nativeApi.GetPlaceholderState(childPath).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.False);
                        Assert.That(nativeApi.GetPlaceholderIdentity(childPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
                        Assert.That(File.ReadAllText(childPath), Is.EqualTo("Unsynced local changes must remain pending."));
                    });
                }
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pair.Id, root);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
