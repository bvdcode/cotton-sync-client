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
        [Test]
        public async Task RunOnceAsync_StopsDirectoryHydrationWhenPinIsRemoved()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/first.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/second.mp3"));
            bool directoryPinned = true;
            Dictionary<string, int> fileReads = new(StringComparer.OrdinalIgnoreCase);
            FakeCloudFilesAdapter cloudFiles = new()
            {
                Hydrating = _ => directoryPinned = false,
            };
            RecordingSyncPairWork inner = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                path =>
                {
                    if (path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase))
                    {
                        return directoryPinned
                            ? CreatePinnedDirectoryDiskState()
                            : CreateNeutralDirectoryDiskState();
                    }

                    if (!directoryPinned)
                    {
                        return path.EndsWith("first.mp3", StringComparison.OrdinalIgnoreCase)
                            ? CreateNeutralHydratedDiskState()
                            : new WindowsVirtualFileDiskState(
                                FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline,
                                Length: 12,
                                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
                    }

                    fileReads.TryGetValue(path, out int readCount);
                    fileReads[path] = readCount + 1;
                    return readCount == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState();
                });

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Music"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Music/first.mp3" }));
                Assert.That(cloudFiles.PinnedPaths, Is.Empty);
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Music" }));
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(stateStore.GetRequired(syncPair.Id, "Music/first.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(stateStore.GetRequired(syncPair.Id, "Music/second.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(diagnostics.Snapshot().Any(static item => item is
                    { Operation: "manual-always-keep-directory", Status: "canceled" }), Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_IgnoresOnlineOnlyFileAfterPinRemoval()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/second.mp3"));
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                new FakeCloudFilesAdapter(),
                readDiskState: _ => new WindowsVirtualFileDiskState(
                    FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline,
                    Length: 12,
                    LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc)));

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Music/second.mp3"]));

            Assert.That(inner.Requests, Is.Empty);
        }

        [Test]
        public async Task RunOnceAsync_ForwardsChangedOnlineOnlyFileAfterPinRemoval()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/second.mp3"));
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                new FakeCloudFilesAdapter
                {
                    PlaceholderState = WindowsCloudFilesPlaceholderState.Placeholder,
                },
                readDiskState: _ => new WindowsVirtualFileDiskState(
                    FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline,
                    Length: 12,
                    LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc)));

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Music/second.mp3"]));

            Assert.That(inner.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task RunOnceAsync_TreatsInterruptedNativeHydrationAsCanceledAfterUnpin()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track.mp3"));
            bool directoryPinned = true;
            FakeCloudFilesAdapter cloudFiles = new()
            {
                Hydrating = _ =>
                {
                    directoryPinned = false;
                    throw new WindowsCloudFilesNativeException(
                        "CfHydratePlaceholder", unchecked((int)0x80070185));
                },
            };
            RecordingSyncPairWork inner = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner, stateStore, cloudFiles, diagnostics: diagnostics,
                readDiskState: path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? directoryPinned ? CreatePinnedDirectoryDiskState() : CreateNeutralDirectoryDiskState()
                    : CreatePinnedRemoteOnlyDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Music"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(stateStore.GetRequired(syncPair.Id, "Music/track.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(diagnostics.Snapshot().Any(static item => item is
                    { Operation: "manual-always-keep", Status: "canceled" }), Is.True);
                Assert.That(diagnostics.Snapshot().Any(static item => item.Status == "failed"), Is.False);
            });
        }
    }
}
