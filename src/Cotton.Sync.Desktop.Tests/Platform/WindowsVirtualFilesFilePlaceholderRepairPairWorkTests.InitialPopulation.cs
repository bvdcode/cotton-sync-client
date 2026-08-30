// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesFilePlaceholderRepairPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_InitialPopulationRepairsOnlyPreexistingTrackedFiles()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            const string existingPath = "Music/existing.mp3";
            const string createdPath = "Music/created.mp3";
            WriteFile(existingPath);
            WriteFile(createdPath);
            FakeSyncStateStore stateStore = new FakeSyncStateStore(
                CreateFileState(syncPair, existingPath, trackedPlaceholder: true));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            cloudFiles.States[existingPath] = WindowsCloudFilesPlaceholderState.Placeholder;
            cloudFiles.States[createdPath] = WindowsCloudFilesPlaceholderState.Placeholder;
            RecordingSyncPairWork inner = new RecordingSyncPairWork
            {
                OnRunAsync = () => stateStore.UpsertAsync(
                    CreateFileState(syncPair, createdPath, trackedPlaceholder: true)),
            };
            WindowsVirtualFilesFilePlaceholderRepairPairWork work =
                new WindowsVirtualFilesFilePlaceholderRepairPairWork(inner, stateStore, cloudFiles);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.InitialPopulation | SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InspectedPaths, Is.EqualTo(new[] { existingPath }));
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { existingPath }));
                Assert.That(cloudFiles.InspectedPaths, Does.Not.Contain(createdPath));
            });
        }
    }
}
