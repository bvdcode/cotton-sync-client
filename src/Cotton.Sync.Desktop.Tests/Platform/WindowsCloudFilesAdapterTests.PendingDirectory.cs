// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public void SetInSyncState_PendingDirectoryClearsNativeStateAndNotifiesExplorer()
        {
            string root = Path.Combine(_tempDirectory, "root");
            string directory = Path.Combine(root, "Photos");
            Directory.CreateDirectory(directory);
            FakeCloudFilesNativeApi nativeApi = new();
            nativeApi.InSyncPaths.Add(directory);
            RecordingShellChangeNotifier shellChanges = new();
            WindowsCloudFilesAdapter adapter = new(CreatePolicy(), nativeApi, shellChangeNotifier: shellChanges, isReparsePoint: _ => true);
            SyncPairSettings pair = CreateSyncPair(root);

            adapter.SetInSyncState(pair, "Photos", inSync: false);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(nativeApi.PendingPaths, Is.EqualTo(new[] { directory }));
                Assert.That(shellChanges.DirectoryUpdates, Is.EqualTo(new[] { directory }));
            });
        }
    }
}
