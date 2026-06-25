// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesAdapterTests
    {
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint CfPlaceholderCreateFlagDisableOnDemandPopulation = 0x00000001;
        private const uint CfPlaceholderCreateFlagMarkInSync = 0x00000002;
        private const uint CfUpdateFlagMarkInSync = 0x00000002;
        private const uint CfUpdateFlagDisableOnDemandPopulation = 0x00000010;
        private const uint CfUpdateFlagAllowPartial = 0x00000400;
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-adapter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void CreatePlaceholderCreateFlags_ForDirectoryMarksFullyPopulated()
        {
            uint flags = InvokeNativeFlagFactory("CreatePlaceholderCreateFlags", isDirectory: true);

            Assert.That(
                flags,
                Is.EqualTo(CfPlaceholderCreateFlagMarkInSync | CfPlaceholderCreateFlagDisableOnDemandPopulation));
        }

        [Test]
        public void CreateUpdateFlags_ForDirectoryMarksFullyPopulated()
        {
            uint flags = InvokeNativeFlagFactory("CreateUpdateFlags", isDirectory: true);

            Assert.That(
                flags,
                Is.EqualTo(CfUpdateFlagMarkInSync | CfUpdateFlagDisableOnDemandPopulation));
        }

        [Test]
        public void CreateUpdateFlags_ForFileAllowsPartialUpdates()
        {
            uint flags = InvokeNativeFlagFactory("CreateUpdateFlags", isDirectory: false);

            Assert.That(
                flags,
                Is.EqualTo(CfUpdateFlagMarkInSync | CfUpdateFlagAllowPartial));
        }

        [Test]
        public void CreateReparseTagOpenFlags_IncludesBackupSemanticsForDirectories()
        {
            string directoryPath = Path.Combine(_tempDirectory, "directory-placeholder");
            Directory.CreateDirectory(directoryPath);

            uint flags = WindowsCloudFilesAdapter.CreateReparseTagOpenFlags(directoryPath);

            Assert.Multiple(() =>
            {
                Assert.That((flags & FileFlagOpenReparsePoint), Is.EqualTo(FileFlagOpenReparsePoint));
                Assert.That((flags & FileFlagBackupSemantics), Is.EqualTo(FileFlagBackupSemantics));
            });
        }

        [Test]
        public void CreateReparseTagOpenFlags_DoesNotIncludeBackupSemanticsForFiles()
        {
            string filePath = Path.Combine(_tempDirectory, "remote-only.txt");
            File.WriteAllText(filePath, string.Empty);

            uint flags = WindowsCloudFilesAdapter.CreateReparseTagOpenFlags(filePath);

            Assert.Multiple(() =>
            {
                Assert.That((flags & FileFlagOpenReparsePoint), Is.EqualTo(FileFlagOpenReparsePoint));
                Assert.That((flags & FileFlagBackupSemantics), Is.EqualTo(0));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RegistersSyncRootAndCreatesChildPlaceholder()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);
            WindowsCloudFilesPlaceholderIdentity fileIdentity =
                WindowsCloudFilesPlaceholderIdentity.Parse(nativeApi.Placeholders[0].FileIdentity);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Registrations[0].LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(nativeApi.Registrations[0].ProviderName, Is.EqualTo(WindowsCloudFilesAdapter.ProviderName));
                Assert.That(nativeApi.Registrations[0].SyncRootIdentity, Is.Not.Empty);
                Assert.That(nativeApi.Placeholders[0].BaseDirectoryPath, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "Projects")));
                Assert.That(nativeApi.Placeholders[0].RelativeFileName, Is.EqualTo("remote-only.txt"));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(target));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Unpinned));
                Assert.That(nativeApi.Placeholders[0].FileSizeBytes, Is.EqualTo(12));
                Assert.That(nativeApi.Placeholders[0].FileIdentity, Is.EqualTo(result.PlaceholderIdentity));
                Assert.That(fileIdentity.RelativePath, Is.EqualTo("Projects/remote-only.txt"));
                Assert.That(fileIdentity.NodeFileId, Is.EqualTo(Guid.Parse("33333333-3333-3333-3333-333333333333")));
                Assert.That(fileIdentity.ContentHash, Is.EqualTo("hash"));
                Assert.That(fileIdentity.ETag, Is.EqualTo("etag"));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(Directory.Exists(Path.Combine(root, "Projects")), Is.True);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RegistersStorageProviderSyncRootBeforeNativeSyncRoot()
        {
            var operations = new List<string>();
            var nativeApi = new FakeCloudFilesNativeApi { OperationLog = operations };
            var storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                storageProviderRegistrar: storageProvider);
            string root = Path.Combine(_tempDirectory, "root");

            adapter.CreateFilePlaceholder(CreateRequest(root, "remote-only.txt"));

            WindowsStorageProviderSyncRootRegistration registration = storageProvider.Registrations.Single();
            Assert.Multiple(() =>
            {
                Assert.That(
                    operations,
                    Is.EqualTo(new[] { "storage-provider-register", "native-register" }));
                Assert.That(registration.SyncPairId, Is.EqualTo(Guid.Parse("11111111-1111-1111-1111-111111111111")));
                Assert.That(registration.LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(registration.ProviderVersion, Is.Not.Empty);
                Assert.That(registration.IconResource, Does.EndWith("Cotton.Sync.Desktop.exe"));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RegistersSyncRootOncePerAdapterForSameRoot()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/first.txt"));
            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/second.txt"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders.Select(static placeholder => placeholder.RelativeFileName), Is.EqualTo(new[] { "first.txt", "second.txt" }));
                Assert.That(nativeApi.PinStates.Select(static pin => pin.PinState), Is.EqualTo(new[] { WindowsCloudFilesPinState.Unpinned, WindowsCloudFilesPinState.Unpinned }));
            });
        }

        [Test]
        public void CreateFilePlaceholders_BatchesNativeCreatesByDirectoryAndReturnsResults()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            RemoteFilePlaceholderRequest[] requests =
            [
                CreateRequest(root, "Projects/first.txt"),
                CreateRequest(root, "Projects/second.txt"),
                CreateRequest(root, "Other/third.txt"),
            ];

            IReadOnlyList<RemoteFilePlaceholderResult> results = adapter.CreateFilePlaceholders(requests);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(3));
                Assert.That(nativeApi.PlaceholderBatches, Has.Count.EqualTo(2));
                Assert.That(nativeApi.PlaceholderBatches[0].Select(static item => item.RelativeFileName), Is.EqualTo(new[] { "first.txt", "second.txt" }));
                Assert.That(nativeApi.PlaceholderBatches[1].Select(static item => item.RelativeFileName), Is.EqualTo(new[] { "third.txt" }));
                Assert.That(results, Has.Count.EqualTo(3));
                Assert.That(results.Select(static result => result.HydrationState), Is.All.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(results.Select(static result => result.PlaceholderIdentity), Is.EqualTo(nativeApi.Placeholders.Select(static placeholder => placeholder.FileIdentity)));
                Assert.That(nativeApi.PinStates.Select(static pin => pin.FilePath), Is.EqualTo(new[]
                {
                    Path.GetFullPath(Path.Combine(root, "Projects", "first.txt")),
                    Path.GetFullPath(Path.Combine(root, "Projects", "second.txt")),
                    Path.GetFullPath(Path.Combine(root, "Other", "third.txt")),
                }));
            });
        }

        [Test]
        public void CreateDirectoryPlaceholder_CreatesRemoteDirectoryPlaceholderWithoutConversion()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects", "Nested"));

            adapter.CreateDirectoryPlaceholder(CreateDirectoryRequest(root, "Projects/Nested"));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single(static item => item.Status == "completed");
            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                System.Text.Json.JsonSerializer.Deserialize<WindowsCloudFilesDirectoryPlaceholderIdentity>(
                    nativeApi.Placeholders.Single().FileIdentity,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders[0].BaseDirectoryPath, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "Projects"))));
                Assert.That(nativeApi.Placeholders[0].RelativeFileName, Is.EqualTo("Nested"));
                Assert.That(nativeApi.Placeholders[0].IsDirectory, Is.True);
                Assert.That(nativeApi.PinStates.Select(static pin => pin.FilePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Unpinned));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[] { "native-set-pin-state", "native-set-in-sync-state" }));
                Assert.That(identity.RelativePath, Is.EqualTo("Projects/Nested"));
                Assert.That(identity.NodeId, Is.EqualTo(Guid.Parse("88888888-8888-8888-8888-888888888888")));
                Assert.That(diagnostic.Operation, Is.EqualTo("create-directory-placeholder"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects/Nested"));
            });
        }

        [Test]
        public void CreateDirectoryPlaceholder_ConvertsNonEmptyExistingDirectoryToCloudFilesPlaceholder()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects", "Nested"));
            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(Path.Combine(directoryPath, "local.txt"), "local");

            adapter.CreateDirectoryPlaceholder(CreateDirectoryRequest(root, "Projects/Nested"));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single(static item => item.Status == "completed");
            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                System.Text.Json.JsonSerializer.Deserialize<WindowsCloudFilesDirectoryPlaceholderIdentity>(
                    nativeApi.ConvertedPlaceholders.Single().FileIdentity,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.ConvertedPlaceholders.Select(static item => item.FilePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.ConvertedPlaceholders[0].IsDirectory, Is.True);
                Assert.That(nativeApi.ConvertedPlaceholders[0].MarkInSync, Is.True);
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.UpdatedPlaceholders[0].BaseDirectoryPath, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "Projects"))));
                Assert.That(nativeApi.UpdatedPlaceholders[0].RelativeFileName, Is.EqualTo("Nested"));
                Assert.That(nativeApi.UpdatedPlaceholders[0].IsDirectory, Is.True);
                Assert.That(nativeApi.PinStates.Select(static pin => pin.FilePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Unpinned));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[] { "native-convert", "native-update", "native-set-pin-state", "native-set-in-sync-state" }));
                Assert.That(identity.RelativePath, Is.EqualTo("Projects/Nested"));
                Assert.That(identity.NodeId, Is.EqualTo(Guid.Parse("88888888-8888-8888-8888-888888888888")));
                Assert.That(diagnostic.Operation, Is.EqualTo("convert-directory-placeholder"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("Projects/Nested"));
            });
        }

        [Test]
        public void CreateDirectoryPlaceholder_RepairsExistingCloudFilesDirectoryPlaceholderWithoutReconversion()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(directoryPath);
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase));

            adapter.CreateDirectoryPlaceholder(CreateDirectoryRequest(root, "Projects"));

            IReadOnlyList<WindowsCloudFilesDiagnosticEvent> events = diagnostics.Snapshot();
            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                System.Text.Json.JsonSerializer.Deserialize<WindowsCloudFilesDirectoryPlaceholderIdentity>(
                    nativeApi.UpdatedPlaceholders.Single().FileIdentity,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.UpdatedPlaceholders[0].BaseDirectoryPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(nativeApi.UpdatedPlaceholders[0].RelativeFileName, Is.EqualTo("Projects"));
                Assert.That(nativeApi.UpdatedPlaceholders[0].IsDirectory, Is.True);
                Assert.That(nativeApi.PinStates.Select(static pin => pin.FilePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.CallLog, Is.EqualTo(new[] { "native-update", "native-set-pin-state", "native-set-in-sync-state" }));
                Assert.That(identity.RelativePath, Is.EqualTo("Projects"));
                Assert.That(identity.NodeId, Is.EqualTo(Guid.Parse("88888888-8888-8888-8888-888888888888")));
                Assert.That(events.Any(static item => item is { Operation: "convert-directory-placeholder", Status: "repaired-placeholder" }), Is.True);
            });
        }

        [Test]
        public void CreateFilePlaceholder_AllowsCloudFilesDirectoryPlaceholderAncestors()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string parent = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(parent);
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), parent, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(Path.GetFullPath(path), parent, StringComparison.OrdinalIgnoreCase));

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/remote-only.txt"));

            Assert.That(nativeApi.Placeholders.Select(static item => item.RelativeFileName), Is.EqualTo(new[] { "remote-only.txt" }));
        }

        [Test]
        public void CreateFilePlaceholder_RetriesTransientPinStatePathOpenFailure()
        {
            var nativeApi = new FakeCloudFilesNativeApi
            {
                PinStateFailuresBeforeSuccess = 2,
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                transientRetryDelay: _ => { });
            string root = Path.Combine(_tempDirectory, "root");

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/remote-only.txt"));

            IReadOnlyList<WindowsCloudFilesDiagnosticEvent> retryEvents = diagnostics.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.PinStateCalls, Is.EqualTo(3));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(retryEvents.Select(static item => item.Status), Is.EqualTo(new[] { "retrying", "retrying" }));
                Assert.That(retryEvents.Select(static item => item.Operation), Is.All.EqualTo("set-pin-state"));
                Assert.That(retryEvents.Select(static item => item.HResult), Is.All.EqualTo(HResultPathNotFound));
            });
        }

        [Test]
        public void CreateFilePlaceholder_UpdatesExistingCloudFilesPlaceholder()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase));
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders[0].BaseDirectoryPath, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "Projects")));
                Assert.That(nativeApi.UpdatedPlaceholders[0].RelativeFileName, Is.EqualTo("remote-only.txt"));
                Assert.That(nativeApi.UpdatedPlaceholders[0].FileIdentity, Is.EqualTo(result.PlaceholderIdentity));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RetriesTransientUpdatePathOpenFailure()
        {
            var nativeApi = new FakeCloudFilesNativeApi
            {
                UpdateFailuresBeforeSuccess = 1,
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase),
                transientRetryDelay: _ => { });
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");

            adapter.CreateFilePlaceholder(request);

            WindowsCloudFilesDiagnosticEvent retryEvent = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UpdateCalls, Is.EqualTo(2));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(retryEvent.Operation, Is.EqualTo("update-placeholder"));
                Assert.That(retryEvent.Status, Is.EqualTo("retrying"));
                Assert.That(retryEvent.HResult, Is.EqualTo(HResultPathNotFound));
            });
        }

        [Test]
        public void UnregisterSyncRoot_ClearsRegistrationCacheForFuturePlaceholderCreation()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
        public void FinalizeUploadedFilePlaceholder_ConvertsRegularUploadedFileAndMarksInSync()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");

            adapter.FinalizeUploadedFilePlaceholder(syncPair, state);

            var converted = nativeApi.ConvertedPlaceholders.Single();
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
            });
        }

        [Test]
        public void FinalizeUploadedFilePlaceholder_WhenPathIsAlreadyPlaceholderMarksInSyncWithoutConversion()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase));
            SyncPairSettings syncPair = CreateSyncPair(root);

            adapter.FinalizeUploadedFilePlaceholder(syncPair, CreateUploadedFileState(syncPair, "Projects/report.txt"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
            });
        }

        [Test]
        public void FinalizeUploadedFilePlaceholder_RejectsMissingRemoteIdentityBeforeNativeCalls()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "uploaded content");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");
            state.RemoteFileManifestId = null;

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.FinalizeUploadedFilePlaceholder(syncPair, state));

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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
            var nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string reparseDirectory = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(reparseDirectory);
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
            var nativeApi = new FakeCloudFilesNativeApi
            {
                RegisterException = new WindowsCloudFilesNativeException("CfRegisterSyncRoot", unchecked((int)0x8007017C)),
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            var handler = new RecordingCallbackHandler();

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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            var handler = new RecordingCallbackHandler();

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
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
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
            var operations = new List<string>();
            var nativeApi = new FakeCloudFilesNativeApi { OperationLog = operations };
            var storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            var adapter = new WindowsCloudFilesAdapter(
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
            var operations = new List<string>();
            var nativeApi = new FakeCloudFilesNativeApi
            {
                OperationLog = operations,
                UnregisterException = new WindowsCloudFilesNativeException("CfUnregisterSyncRoot", HResultPathNotFound),
            };
            var storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);

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
            var nativeApi = new FakeCloudFilesNativeApi
            {
                UnregisterException = new WindowsCloudFilesNativeException("CfUnregisterSyncRoot", unchecked((int)0x8007017C)),
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
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

        [Test]
        public void DehydratePlaceholder_UsesSafeRootAndRelativePathThroughNativeBoundary()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi, diagnostics: diagnostics);
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
        public void SetInSyncState_ForwardsDirectoryPlaceholderToNativeBoundary()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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

        [Test]
        public void SetInSyncState_FailsWhenDirectoryStillReportsPartialState()
        {
            var nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet = WindowsCloudFilesPlaceholderState.InSync | WindowsCloudFilesPlaceholderState.Partial,
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var shellChanges = new RecordingShellChangeNotifier();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var shellChanges = new RecordingShellChangeNotifier();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
        public void SetSyncRootInSyncState_ForwardsRootToNativeBoundary()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet = WindowsCloudFilesPlaceholderState.SyncRoot,
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet =
                    WindowsCloudFilesPlaceholderState.SyncRoot
                    | WindowsCloudFilesPlaceholderState.InSync
                    | WindowsCloudFilesPlaceholderState.Partial,
            };
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var adapter = new WindowsCloudFilesAdapter(
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
            var nativeApi = new FakeCloudFilesNativeApi();
            var shellChanges = new RecordingShellChangeNotifier();
            var adapter = new WindowsCloudFilesAdapter(
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

        [Test]
        public void FinalizeUploadedFilePlaceholder_NotifiesExplorerAfterUploadedFileStatusFinalization()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var shellChanges = new RecordingShellChangeNotifier();
            var adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string filePath = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "local");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");

            adapter.FinalizeUploadedFilePlaceholder(syncPair, state);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { filePath }));
                Assert.That(shellChanges.ItemUpdates, Is.EqualTo(new[] { filePath }));
                Assert.That(shellChanges.DirectoryUpdates, Is.Empty);
            });
        }

        [Test]
        public void TransferData_ForwardsToNativeBoundary()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            var request = new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(3),
                [],
                5,
                0,
                5,
                0,
                5,
                null,
                0);
            WindowsCloudFilesTransferData transfer = WindowsCloudFilesTransferData.Success(
                request,
                Encoding.UTF8.GetBytes("hello"),
                0,
                5);

            adapter.TransferData(transfer);

            Assert.That(nativeApi.Transfers, Is.EqualTo(new[] { transfer }));
        }

        private static uint InvokeNativeFlagFactory(string methodName, bool isDirectory)
        {
            System.Reflection.MethodInfo? method = typeof(WindowsCloudFilesNativeApi).GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            object? result = method!.Invoke(null, [isDirectory]);
            Assert.That(result, Is.Not.Null);
            return Convert.ToUInt32(result);
        }

        private WindowsVirtualFilesRootSafetyPolicy CreatePolicy()
        {
            return new WindowsVirtualFilesRootSafetyPolicy(
                _ => string.Empty,
                () => _tempDirectory);
        }

        private static SyncPairSettings CreateSyncPair(string root)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Windows virtual files",
                LocalRootPath = root,
                RemoteDisplayPath = "/",
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
            };
        }

        private static RemoteFilePlaceholderRequest CreateRequest(
            string localRootPath,
            string relativePath,
            string syncPairId = "11111111-1111-1111-1111-111111111111")
        {
            return new RemoteFilePlaceholderRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = 12,
                    ContentHash = "hash",
                    ETag = "etag",
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private static SyncStateEntry CreateUploadedFileState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = "uploaded-hash",
                LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc),
                LocalSizeBytes = 16,
                RemoteSizeBytes = 16,
                RemoteNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                RemoteFileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RemoteFileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                RemoteOriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                RemoteContentHash = "uploaded-hash",
                RemoteETag = "uploaded-etag",
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 06, 30, DateTimeKind.Utc),
            };
        }

        private static RemoteDirectoryMaterializationRequest CreateDirectoryRequest(
            string localRootPath,
            string relativePath,
            string syncPairId = "11111111-1111-1111-1111-111111111111")
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeDto
                {
                    Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Name = Path.GetFileName(relativePath),
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                });
        }

        private sealed class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<string>? OperationLog { get; init; }

            public List<string> CallLog { get; } = [];

            public List<WindowsCloudFilesNativeSyncRootRegistration> Registrations { get; } = [];

            public List<WindowsCloudFilesNativePlaceholder> Placeholders { get; } = [];

            public List<WindowsCloudFilesNativePlaceholder> UpdatedPlaceholders { get; } = [];

            public List<ConvertedPlaceholderCall> ConvertedPlaceholders { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public WindowsCloudFilesPlaceholderState InSyncStateAfterSet { get; set; } =
                WindowsCloudFilesPlaceholderState.Placeholder | WindowsCloudFilesPlaceholderState.InSync;

            public List<PinStateCall> PinStates { get; } = [];

            public List<WindowsCloudFilesConnectionRequest> ConnectionRequests { get; } = [];

            public List<string> UnregisteredRoots { get; } = [];

            public List<WindowsCloudFilesConnectionKey> DisconnectedKeys { get; } = [];

            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public List<WindowsCloudFilesAckDehydrateData> Dehydrates { get; } = [];

            public List<string> DehydratedPaths { get; } = [];

            public Exception? RegisterException { get; set; }

            public Exception? UnregisterException { get; set; }

            public int UpdateFailuresBeforeSuccess { get; set; }

            public int UpdateCalls { get; private set; }

            public int PinStateFailuresBeforeSuccess { get; set; }

            public int PinStateCalls { get; private set; }

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
                OperationLog?.Add("native-register");
                Registrations.Add(registration);
                if (RegisterException is not null)
                {
                    throw RegisterException;
                }
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                OperationLog?.Add("native-unregister");
                UnregisteredRoots.Add(localRootPath);
                if (UnregisterException is not null)
                {
                    throw UnregisterException;
                }
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                Placeholders.Add(placeholder);
            }

            public List<IReadOnlyList<WindowsCloudFilesNativePlaceholder>> PlaceholderBatches { get; } = [];

            public void CreatePlaceholders(IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
            {
                PlaceholderBatches.Add(placeholders.ToArray());
                Placeholders.AddRange(placeholders);
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                CallLog.Add("native-update");
                UpdateCalls++;
                if (UpdateFailuresBeforeSuccess > 0)
                {
                    UpdateFailuresBeforeSuccess--;
                    throw new WindowsCloudFilesNativeException("CreateFile", HResultPathNotFound);
                }

                UpdatedPlaceholders.Add(placeholder);
            }

            public void ConvertToPlaceholder(string filePath, byte[] fileIdentity, bool isDirectory, bool markInSync)
            {
                CallLog.Add("native-convert");
                ConvertedPlaceholders.Add(new ConvertedPlaceholderCall(filePath, fileIdentity, isDirectory, markInSync));
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
            {
                CallLog.Add("native-set-pin-state");
                PinStateCalls++;
                if (PinStateFailuresBeforeSuccess > 0)
                {
                    PinStateFailuresBeforeSuccess--;
                    throw new WindowsCloudFilesNativeException("CreateFile", HResultPathNotFound);
                }

                PinStates.Add(new PinStateCall(filePath, pinState));
            }

            public void SetInSyncState(string filePath)
            {
                CallLog.Add("native-set-in-sync-state");
                InSyncPaths.Add(filePath);
            }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath)
            {
                return InSyncPaths.Contains(filePath, StringComparer.OrdinalIgnoreCase)
                    ? InSyncStateAfterSet
                    : WindowsCloudFilesPlaceholderState.None;
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                ConnectionRequests.Add(request);
                return new WindowsCloudFilesConnection(
                    request.LocalRootPath,
                    new WindowsCloudFilesConnectionKey(42),
                    DisconnectSyncRoot);
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
                DisconnectedKeys.Add(connectionKey);
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer);
            }

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                Dehydrates.Add(dehydrate);
            }

            public void DehydratePlaceholder(string filePath)
            {
                DehydratedPaths.Add(filePath);
            }

            public sealed record PinStateCall(string FilePath, WindowsCloudFilesPinState PinState);

            public sealed record ConvertedPlaceholderCall(
                string FilePath,
                byte[] FileIdentity,
                bool IsDirectory,
                bool MarkInSync);
        }

        private sealed class RecordingShellChangeNotifier : IWindowsShellChangeNotifier
        {
            public List<string> ItemUpdates { get; } = [];

            public List<string> DirectoryUpdates { get; } = [];

            public void NotifyItemUpdated(string path)
            {
                ItemUpdates.Add(path);
            }

            public void NotifyDirectoryUpdated(string path)
            {
                DirectoryUpdates.Add(path);
            }
        }

        private sealed class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            private readonly List<string> _operationLog;

            public FakeStorageProviderSyncRootRegistrar(List<string> operationLog)
            {
                _operationLog = operationLog;
            }

            public List<WindowsStorageProviderSyncRootRegistration> Registrations { get; } = [];

            public List<Guid> UnregisteredSyncPairIds { get; } = [];

            public List<string> UnregisteredLocalRootPaths { get; } = [];

            public int UnregisterAllCalls { get; private set; }

            public bool IsSupported()
            {
                return true;
            }

            public bool IsRegistered(Guid syncPairId)
            {
                return Registrations.Any(registration => registration.SyncPairId == syncPairId);
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                _operationLog.Add("storage-provider-register");
                Registrations.Add(registration);
            }

            public void Unregister(Guid syncPairId, string localRootPath)
            {
                _operationLog.Add("storage-provider-unregister");
                UnregisteredSyncPairIds.Add(syncPairId);
                UnregisteredLocalRootPaths.Add(localRootPath);
            }

            public void UnregisterAllForCurrentUser()
            {
                _operationLog.Add("storage-provider-unregister-all");
                UnregisterAllCalls++;
            }
        }

        private sealed class RecordingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }
    }
}
