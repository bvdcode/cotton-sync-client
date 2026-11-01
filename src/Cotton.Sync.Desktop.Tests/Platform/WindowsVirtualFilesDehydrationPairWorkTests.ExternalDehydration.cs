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
        [TestCase(FileAttributes.Offline)]
        [TestCase((FileAttributes)FileAttributeRecallOnDataAccess)]
        public async Task RunOnceAsync_RecordsExternalDehydrationWithoutReadingOrTransferringContent(
            FileAttributes onlineOnlyAttribute)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            SyncStateEntry state = CreateHydratedExternalDehydrationState(pair);
            FakeSyncStateStore store = new();
            store.UpsertEntry(state);
            RecordingSyncPairWork inner = new();
            FakeCloudFilesAdapter cloudFiles = new();
            FakeContentHasher hasher = new("unexpected-content");
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, hasher, diagnostics,
                _ => CreateExternallyDehydratedDiskState(onlineOnlyAttribute));

            await work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths([state.RelativePath]));

            Assert.Multiple(() =>
            {
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                Assert.That(state.LocalContentHash, Is.Null);
                Assert.That(state.LocalSizeBytes, Is.Null);
                Assert.That(state.LocalLastWriteUtc, Is.Null);
                Assert.That(state.RemoteContentHash, Is.EqualTo("remote-hash"));
                Assert.That(state.PlaceholderIdentity, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(hasher.Calls, Is.Zero);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
                Assert.That(diagnostics.Snapshot().Single().Operation, Is.EqualTo("external-dehydration"));
            });
        }

        [Test]
        public async Task RunOnceAsync_ExternalDehydrationPreservesUnrelatedChanges()
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            SyncStateEntry state = CreateHydratedExternalDehydrationState(pair);
            FakeSyncStateStore store = new();
            store.UpsertEntry(state);
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, new FakeCloudFilesAdapter(),
                readDiskState: _ => CreateExternallyDehydratedDiskState(FileAttributes.Offline));

            await work.RunOnceAsync(pair,
                SyncRunRequest.ForLocalChangedPaths([state.RelativePath, "Docs/new.txt"]));

            Assert.That(inner.Requests.Single().LocalChangedPaths, Is.EqualTo(new[] { "Docs/new.txt" }));
            Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
        }

        [TestCase(SyncRunCause.Periodic)]
        [TestCase(SyncRunCause.Resume)]
        [TestCase(SyncRunCause.Manual)]
        [TestCase(SyncRunCause.CheckNow)]
        [TestCase(SyncRunCause.InternalMaintenance)]
        [TestCase(SyncRunCause.LocalWatcherError)]
        [TestCase(SyncRunCause.LocalChangeOverflow)]
        public async Task RunOnceAsync_RecoversExternalDehydrationWithoutAFileEvent(SyncRunCause cause)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            SyncStateEntry state = CreateHydratedExternalDehydrationState(pair);
            FakeSyncStateStore store = new();
            store.UpsertEntry(state);
            RecordingSyncPairWork inner = new();
            FakeCloudFilesAdapter cloudFiles = new();
            FakeContentHasher hasher = new("unexpected-content");
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, hasher,
                readDiskState: _ => CreateExternallyDehydratedDiskState(FileAttributes.Offline));

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(cause));

            Assert.Multiple(() =>
            {
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                Assert.That(inner.Requests.Single().IsFull, Is.True);
                Assert.That(inner.Requests.Single().Causes, Is.EqualTo(cause));
                Assert.That(hasher.Calls, Is.Zero);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_MaintenanceRecoversDehydrationAfterStartupRecovery()
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            SyncStateEntry state = CreateHydratedExternalDehydrationState(pair);
            FakeSyncStateStore store = new();
            store.UpsertEntry(state);
            bool onlineOnly = false;
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                new RecordingSyncPairWork(), store, new FakeCloudFilesAdapter(),
                readDiskState: _ => onlineOnly
                    ? CreateExternallyDehydratedDiskState(FileAttributes.Offline)
                    : CreateNeutralHydratedDiskState());

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            onlineOnly = true;
            await work.RunOnceAsync(pair,
                SyncRunRequest.ForFull(SyncRunCause.Periodic | SyncRunCause.InternalMaintenance));

            Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, false, false, true)]
        public async Task RunOnceAsync_PreservesUnverifiedOnlineOnlyState(
            bool inSync, bool foreignIdentity, bool changedSize, bool changedTimestamp)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            SyncStateEntry state = CreateHydratedExternalDehydrationState(pair);
            if (foreignIdentity)
            {
                state.PlaceholderIdentity = [9];
            }
            FakeSyncStateStore store = new();
            store.UpsertEntry(state);
            RecordingSyncPairWork inner = new();
            WindowsCloudFilesPlaceholderState nativeState = WindowsCloudFilesPlaceholderState.Placeholder;
            if (inSync)
            {
                nativeState |= WindowsCloudFilesPlaceholderState.InSync;
            }
            FakeCloudFilesAdapter cloudFiles = new() { PlaceholderState = nativeState };
            WindowsVirtualFileDiskState disk = CreateExternallyDehydratedDiskState(FileAttributes.Offline);
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles,
                readDiskState: _ => new WindowsVirtualFileDiskState(disk.Attributes,
                    changedSize ? disk.Length + 1 : disk.Length,
                    changedTimestamp ? disk.LastWriteUtc.AddSeconds(1) : disk.LastWriteUtc));

            await work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths([state.RelativePath]));

            Assert.Multiple(() =>
            {
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(state.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(inner.Requests.Single().LocalChangedPaths, Is.EqualTo(new[] { state.RelativePath }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
            });
        }

        private static SyncStateEntry CreateHydratedExternalDehydrationState(SyncPairSettings pair)
        {
            SyncStateEntry state = CreatePlaceholderState(pair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = state.RemoteContentHash;
            state.LocalSizeBytes = state.RemoteSizeBytes;
            state.LocalLastWriteUtc = new DateTime(2026, 6, 16, 10, 5, 0, DateTimeKind.Utc);
            return state;
        }

        private static WindowsVirtualFileDiskState CreateExternallyDehydratedDiskState(FileAttributes onlineOnlyAttribute)
        {
            return new WindowsVirtualFileDiskState(
                FileAttributes.Archive | FileAttributes.ReparsePoint | onlineOnlyAttribute,
                12, new DateTime(2026, 6, 16, 10, 5, 0, DateTimeKind.Utc));
        }
    }
}
