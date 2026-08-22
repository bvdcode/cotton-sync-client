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
        public void SetInSyncState_FailsWhenDirectoryStillReportsPartialState()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet = WindowsCloudFilesPlaceholderState.InSync | WindowsCloudFilesPlaceholderState.Partial,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges,
                diagnostics: diagnostics,
                isReparsePoint: _ => true);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            SyncPairSettings syncPair = CreateSyncPair(root);

            InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(
                () => adapter.SetInSyncState(syncPair, "Projects"));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("fully populated state"));
                Assert.That(exception?.Message, Does.Contain("Partial"));
                Assert.That(
                    nativeApi.InSyncPaths,
                    Is.EqualTo(new[] { Path.GetFullPath(Path.Combine(root, "Projects")) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects"));
                Assert.That(shellChanges.DirectoryUpdates, Is.Empty);
                Assert.That(shellChanges.ItemUpdates, Is.Empty);
            });
        }

        [Test]
        public void SetInSyncState_NotifiesExplorerAfterDirectoryStatusFinalization()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges,
                isReparsePoint: _ => true);
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(directoryPath);
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetInSyncState(syncPair, "Projects");

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
                Assert.That(shellChanges.DirectoryUpdates, Is.EqualTo(new[] { directoryPath }));
                Assert.That(shellChanges.ItemUpdates, Is.Empty);
            });
        }

        [Test]
        public void SetInSyncState_SkipsNonPlaceholderFileWhenReparseHeuristicIsFalse()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.Combine(root, "Projects", "local.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "local");
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetInSyncState(syncPair, "Projects/local.txt");

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(diagnostic.Operation, Is.EqualTo("set-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("skipped"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects/local.txt"));
            });
        }

        [Test]
        public void PlaceholderIdentityMethods_UseValidatedPathAndNotifyExplorer()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            RecordingShellChangeNotifier shellChanges = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            nativeApi.PlaceholderIdentities[target] = [1, 2, 3];
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges);
            SyncPairSettings syncPair = CreateSyncPair(root);

            byte[] identity = adapter.GetPlaceholderIdentity(syncPair, "Projects/remote-only.txt");
            adapter.UpdatePlaceholderIdentity(syncPair, "Projects/remote-only.txt", [4, 5, 6]);

            Assert.Multiple(() =>
            {
                Assert.That(identity, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(nativeApi.IdentityUpdatedPaths, Is.EqualTo(new[] { target }));
                Assert.That(nativeApi.PlaceholderIdentities[target], Is.EqualTo(new byte[] { 4, 5, 6 }));
                Assert.That(shellChanges.ItemUpdates, Is.EqualTo(new[] { target }));
            });
        }

        [Test]
        public void SetSyncRootInSyncState_ForwardsRootToNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(root);
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetSyncRootInSyncState(syncPair);

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-sync-root-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo(syncPair.Id.ToString()));
                Assert.That(diagnostic.RelativePath, Is.Null);
            });
        }

        [Test]
        public void SetSyncRootInSyncState_FailsWhenNativeStateDoesNotReportInSync()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet = WindowsCloudFilesPlaceholderState.SyncRoot,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(root);
            SyncPairSettings syncPair = CreateSyncPair(root);

            InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(
                () => adapter.SetSyncRootInSyncState(syncPair));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("did not report in-sync state"));
                Assert.That(exception?.Message, Does.Contain("SyncRoot"));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-sync-root-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.RelativePath, Is.Null);
            });
        }

        [Test]
        public void SetSyncRootInSyncState_AllowsRootAggregatePartialState()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet =
                    WindowsCloudFilesPlaceholderState.SyncRoot
                    | WindowsCloudFilesPlaceholderState.InSync
                    | WindowsCloudFilesPlaceholderState.Partial,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(root);
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetSyncRootInSyncState(syncPair);

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("set-sync-root-in-sync-state"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.RelativePath, Is.Null);
            });
        }

        [Test]
        public void SetSyncRootInSyncState_NotifiesExplorerAfterRootStatusFinalization()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges);
            string root = Path.Combine(_tempDirectory, "root");
            Directory.CreateDirectory(root);
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.SetSyncRootInSyncState(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(shellChanges.DirectoryUpdates, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(shellChanges.ItemUpdates, Is.Empty);
            });
        }
    }
}
