// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_HydratesPinnedRemoteOnlyPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            int diskReads = 0;
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => diskReads++ == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/report.txt") }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-always-keep"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithAlreadyHydratedPinnedNotInSyncPlaceholderForwardsPotentialEdit()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalSizeBytes = 12;
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new()
            {
                PlaceholderState = WindowsCloudFilesPlaceholderState.Placeholder,
            };
            RecordingSyncPairWork inner = new();
            RecordingLocalChangeSuppression suppression = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: _ => CreatePinnedHydratedDiskState(),
                localChangeSuppression: suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(suppression.SuppressedWrites, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithAlreadyHydratedPinnedInSyncPlaceholderSuppressesAvailabilityRepeat()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalSizeBytes = 12;
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                readDiskState: _ => CreatePinnedHydratedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(
                    diagnostics.Snapshot().Select(static item => (item.Operation, item.Status)),
                    Does.Contain(("manual-always-keep", "completed")));
            });
        }

        [Test]
        public async Task RunOnceAsync_PinnedRegularFileEditRunsInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalSizeBytes = 12;
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-local-hash"),
                readDiskState: _ => CreatePinnedRegularFileDiskState());
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.EqualTo(new[] { request }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_RecordsCompletedOnDemandHydrationAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => CreateMaterializedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("on-demand-hydration"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesMaterializedPathToInnerSyncWhenContentDiffers()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-hash"),
                diagnostics,
                _ => CreateMaterializedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(updated.LocalContentHash, Is.Null);
                Assert.That(diagnostics.Snapshot(), Is.Empty);
            });
        }

    }
}
