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
        [Test]
        public void DehydratePlaceholder_UsesSafeRootAndRelativePathThroughNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
            string root = Path.Combine(_tempDirectory, "root");
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.DehydratePlaceholder(syncPair, "Projects/remote-only.txt");

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(
                    nativeApi.DehydratedPaths,
                    Is.EqualTo(new[] { Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt")) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("dehydrate-placeholder"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo(syncPair.Id.ToString()));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects/remote-only.txt"));
            });
        }

        [Test]
        public async Task DehydratePlaceholderIfContentMatchesAsync_WhenContentChangedDoesNotDehydrate()
        {
            FakeCloudFilesNativeApi nativeApi = new()
            {
                DehydrationContentMatches = false,
            };
            WindowsCloudFilesAdapter adapter = new(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            SyncPairSettings syncPair = CreateSyncPair(root);
            int validationCallbacks = 0;

            bool dehydrated = await adapter.DehydratePlaceholderIfContentMatchesAsync(
                syncPair,
                "Projects/changed.txt",
                "expected-hash",
                () => validationCallbacks++);

            Assert.Multiple(() =>
            {
                Assert.That(dehydrated, Is.False);
                Assert.That(validationCallbacks, Is.Zero);
                Assert.That(nativeApi.DehydratedPaths, Is.Empty);
            });
        }

        [Test]
        public void HydratePlaceholder_HydratesPinsMarksInSyncAndNotifiesShell()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase));
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.HydratePlaceholder(syncPair, "Projects/remote-only.txt");

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { target }));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(target));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Pinned));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
                Assert.That(shellChanges.ItemUpdates, Is.EqualTo(new[] { target }));
                Assert.That(shellChanges.DirectoryUpdates, Is.Empty);
                Assert.That(nativeApi.CallLog, Is.EqualTo(new[]
                {
                    "native-hydrate",
                    "native-set-pin-state",
                    "native-set-in-sync-state",
                }));
            });
        }

        [Test]
        public void FinalizeUploadedFilePlaceholder_WhenLocalFileChangedRejectsFinalization()
        {
            FakeCloudFilesNativeApi nativeApi = new()
            {
                FinalizationSucceeds = false,
            };
            WindowsCloudFilesAdapter adapter = new(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "changed after upload");
            SyncPairSettings syncPair = CreateSyncPair(root);

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => adapter.FinalizeUploadedFilePlaceholderAsync(
                    syncPair,
                    CreateUploadedFileState(syncPair, "Projects/report.txt")));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Reason, Does.Contain("changed after upload"));
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
            });
        }

        [Test]
        public void PinPlaceholder_PinsDirectoryAndNotifiesShell()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Music", "Album"));
            Directory.CreateDirectory(target);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                shellChangeNotifier: shellChanges,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase));
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.PinPlaceholder(syncPair, "Music/Album");

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(target));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Pinned));
                Assert.That(shellChanges.DirectoryUpdates, Is.EqualTo(new[] { target }));
                Assert.That(diagnostic.Operation, Is.EqualTo("pin-placeholder"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public void SetInSyncState_ForwardsDirectoryPlaceholderToNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => true);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetInSyncState(syncPair, "Projects");

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(
                    nativeApi.InSyncPaths,
                    Is.EqualTo(new[] { Path.GetFullPath(Path.Combine(root, "Projects")) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo(syncPair.Id.ToString()));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects"));
            });
        }

        [Test]
        public void SetInSyncState_ForwardsDirectoryWhenReparseHeuristicIsFalse()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetInSyncState(syncPair, "Projects");

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(
                    nativeApi.InSyncPaths,
                    Is.EqualTo(new[] { Path.GetFullPath(Path.Combine(root, "Projects")) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects"));
            });
        }
    }
}
