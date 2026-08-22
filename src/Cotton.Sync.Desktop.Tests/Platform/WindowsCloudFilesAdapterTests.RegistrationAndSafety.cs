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
        public void UnregisterSyncRoot_ClearsRegistrationCacheForFuturePlaceholderCreation()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/first.txt"));
            adapter.UnregisterSyncRoot(syncPair);
            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/second.txt"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UnregisteredRoots, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(2));
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task FinalizeUploadedFilePlaceholder_ConvertsRegularUploadedFileAndMarksInSync()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");

            RemoteFilePlaceholderResult result = await adapter.FinalizeUploadedFilePlaceholderAsync(syncPair, state);

            FakeCloudFilesNativeApi.ConvertedPlaceholderCall converted =
                nativeApi.ConvertedPlaceholders.Single();
            WindowsCloudFilesPlaceholderIdentity identity =
                WindowsCloudFilesPlaceholderIdentity.Parse(converted.FileIdentity);
            Assert.Multiple(() =>
            {
                Assert.That(converted.FilePath, Is.EqualTo(target));
                Assert.That(converted.IsDirectory, Is.False);
                Assert.That(converted.MarkInSync, Is.True);
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
                Assert.That(identity.RelativePath, Is.EqualTo("Projects/report.txt"));
                Assert.That(identity.NodeFileId, Is.EqualTo(state.RemoteFileId));
                Assert.That(identity.NodeId, Is.EqualTo(state.RemoteNodeId));
                Assert.That(identity.FileManifestId, Is.EqualTo(state.RemoteFileManifestId));
                Assert.That(identity.OriginalNodeFileId, Is.EqualTo(state.RemoteOriginalNodeFileId));
                Assert.That(identity.SizeBytes, Is.EqualTo(state.RemoteSizeBytes));
                Assert.That(identity.ContentHash, Is.EqualTo(state.RemoteContentHash));
                Assert.That(identity.ETag, Is.EqualTo(state.RemoteETag));
                Assert.That(result.PlaceholderIdentity, Is.EqualTo(converted.FileIdentity));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(result.LocalSizeBytes, Is.EqualTo(new FileInfo(target).Length));
                Assert.That(result.LocalLastWriteUtc, Is.EqualTo(new FileInfo(target).LastWriteTimeUtc));
            });
        }

        [Test]
        public async Task FinalizeUploadedFilePlaceholder_WhenPathIsAlreadyPlaceholderUpdatesIdentityAndMarksInSync()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase));
            SyncPairSettings syncPair = CreateSyncPair(root);

            RemoteFilePlaceholderResult result = await adapter.FinalizeUploadedFilePlaceholderAsync(
                syncPair,
                CreateUploadedFileState(syncPair, "Projects/report.txt"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.UpdatedPlaceholders[0].FileIdentity, Is.EqualTo(result.PlaceholderIdentity));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
                Assert.That(result.PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(result.LocalSizeBytes, Is.EqualTo(new FileInfo(target).Length));
                Assert.That(result.LocalLastWriteUtc, Is.EqualTo(new FileInfo(target).LastWriteTimeUtc));
            });
        }

        [Test]
        public async Task FinalizeUploadedFilePlaceholder_RejectsMissingRemoteIdentityBeforeNativeCalls()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");
            state.RemoteFileManifestId = null;

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.FinalizeUploadedFilePlaceholderAsync(syncPair, state));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("remote file identity"));
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsDotSegmentsBeforeNativeCalls()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), @"Projects\..\outside.txt");

            Assert.Throws<SyncPathValidationException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsReparsePointAncestorsBeforeNativeCalls()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string reparseDirectory = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(reparseDirectory);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), reparseDirectory, StringComparison.OrdinalIgnoreCase));
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("reparse point"));
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsOversizedIdentityBeforeNativeCalls()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string longPath = string.Join("/", Enumerable.Range(0, 24).Select(index => "segment-" + index.ToString("D2").PadRight(180, 'x'))) + "/file.txt";
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), longPath);

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("4 KB"));
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsInvalidSyncPairIdBeforeNativeCalls()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), "remote-only.txt", syncPairId: "not-a-guid");

            ArgumentException? exception =
                Assert.Throws<ArgumentException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("sync pair id"));
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_PropagatesNativeCloudFilesFailures()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                RegisterException = new WindowsCloudFilesNativeException("CfRegisterSyncRoot", unchecked((int)0x8007017C)),
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), "remote-only.txt");

            WindowsCloudFilesNativeException? exception =
                Assert.Throws<WindowsCloudFilesNativeException>(() => adapter.CreateFilePlaceholder(request));
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Operation, Is.EqualTo("CfRegisterSyncRoot"));
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Is.Empty);
                Assert.That(diagnostic.Operation, Is.EqualTo("register-sync-root"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo("11111111-1111-1111-1111-111111111111"));
                Assert.That(diagnostic.HResult, Is.EqualTo(unchecked((int)0x8007017C)));
            });
        }

        [Test]
        public void ConnectSyncRoot_ConnectsSafeRootThroughNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            RecordingCallbackHandler handler = new RecordingCallbackHandler();

            using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(CreateSyncPair(root), handler);

            Assert.Multiple(() =>
            {
                Assert.That(connection.LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(connection.ConnectionKey.Value, Is.EqualTo(42));
                Assert.That(nativeApi.ConnectionRequests, Has.Count.EqualTo(1));
                Assert.That(nativeApi.ConnectionRequests[0].LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(nativeApi.ConnectionRequests[0].CallbackHandler, Is.SameAs(handler));
                Assert.That(nativeApi.DisconnectedKeys, Is.Empty);
            });

            connection.Dispose();
            connection.Dispose();

            Assert.That(nativeApi.DisconnectedKeys, Is.EqualTo(new[] { new WindowsCloudFilesConnectionKey(42) }));
        }

        [Test]
        public void ConnectSyncRoot_RejectsUnsafeRootBeforeNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RecordingCallbackHandler handler = new RecordingCallbackHandler();

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.ConnectSyncRoot(CreateSyncPair(@"C:\"), handler));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("drive"));
                Assert.That(nativeApi.ConnectionRequests, Is.Empty);
            });
        }

        [Test]
        public void UnregisterSyncRoot_UsesSafeRegisteredRootThroughNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
            string root = Path.Combine(_tempDirectory, "root");
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.UnregisterSyncRoot(syncPair);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UnregisteredRoots, Is.EqualTo(new[] { Path.GetFullPath(root) }));
                Assert.That(diagnostic.Operation, Is.EqualTo("unregister-sync-root"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo(syncPair.Id.ToString()));
                Assert.That(diagnostic.LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
            });
        }

        [Test]
        public void UnregisterSyncRoot_UnregistersStorageProviderSyncRoot()
        {
            List<string> operations = new List<string>();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi { OperationLog = operations };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                storageProviderRegistrar: storageProvider);
            SyncPairSettings syncPair = CreateSyncPair(Path.Combine(_tempDirectory, "root"));

            adapter.UnregisterSyncRoot(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(
                    operations,
                    Is.EqualTo(new[] { "native-unregister", "storage-provider-unregister" }));
                Assert.That(storageProvider.UnregisteredSyncPairIds, Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(storageProvider.UnregisteredLocalRootPaths, Is.EqualTo(new[] { Path.GetFullPath(syncPair.LocalRootPath) }));
            });
        }

        [Test]
        public void UnregisterSyncRoot_UnregistersStorageProviderWhenNativeRootIsAlreadyMissing()
        {
            List<string> operations = new List<string>();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                OperationLog = operations,
                UnregisterException = new WindowsCloudFilesNativeException("CfUnregisterSyncRoot", HResultPathNotFound),
            };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                storageProviderRegistrar: storageProvider,
                diagnostics: diagnostics);
            SyncPairSettings syncPair = CreateSyncPair(Path.Combine(_tempDirectory, "root"));

            adapter.UnregisterSyncRoot(syncPair);
            IReadOnlyList<WindowsCloudFilesDiagnosticEvent> events = diagnostics.Snapshot();

            Assert.Multiple(() =>
            {
                Assert.That(
                    operations,
                    Is.EqualTo(new[] { "native-unregister", "storage-provider-unregister" }));
                Assert.That(storageProvider.UnregisteredSyncPairIds, Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(storageProvider.UnregisteredLocalRootPaths, Is.EqualTo(new[] { Path.GetFullPath(syncPair.LocalRootPath) }));
                Assert.That(events.Select(static item => item.Operation), Is.EqualTo(new[] { "unregister-sync-root", "unregister-sync-root" }));
                Assert.That(events.Select(static item => item.Status), Is.EqualTo(new[] { "skipped", "completed" }));
                Assert.That(events[0].HResult, Is.EqualTo(HResultPathNotFound));
            });
        }

        [Test]
        public void UnregisterSyncRoot_RejectsUnsafeRootBeforeNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.UnregisterSyncRoot(CreateSyncPair(@"C:\")));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("drive"));
                Assert.That(nativeApi.UnregisteredRoots, Is.Empty);
            });
        }

        [Test]
        public void UnregisterSyncRoot_PropagatesNativeCloudFilesFailures()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                UnregisterException = new WindowsCloudFilesNativeException("CfUnregisterSyncRoot", unchecked((int)0x8007017C)),
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
            SyncPairSettings syncPair = CreateSyncPair(Path.Combine(_tempDirectory, "root"));

            WindowsCloudFilesNativeException? exception =
                Assert.Throws<WindowsCloudFilesNativeException>(() => adapter.UnregisterSyncRoot(syncPair));
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Operation, Is.EqualTo("CfUnregisterSyncRoot"));
                Assert.That(nativeApi.UnregisteredRoots, Has.Count.EqualTo(1));
                Assert.That(diagnostic.Operation, Is.EqualTo("unregister-sync-root"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.SyncPairId, Is.EqualTo(syncPair.Id.ToString()));
                Assert.That(diagnostic.HResult, Is.EqualTo(unchecked((int)0x8007017C)));
            });
        }
    }
}
