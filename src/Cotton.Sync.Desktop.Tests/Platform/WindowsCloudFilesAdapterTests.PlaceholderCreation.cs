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
        public void CreateFilePlaceholder_RegistersSyncRootAndCreatesChildPlaceholder()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
        public void RestoreMissingFilePlaceholder_RecreatesPlaceholderFromPersistedIdentity()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesAdapter adapter = new(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            SyncPairSettings syncPair = CreateSyncPair(root);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/missing.txt");
            byte[] persistedIdentity = WindowsCloudFilesPlaceholderIdentity
                .Create(request, "Projects/missing.txt")
                .ToBytes();
            SyncStateEntry state = new()
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = "Projects/missing.txt",
                Kind = SyncEntryKind.File,
                PlaceholderIdentity = persistedIdentity,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = request.RemoteFile.UpdatedAt,
            };

            RemoteFilePlaceholderResult result = adapter.RestoreMissingFilePlaceholder(syncPair, state);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders[0].RelativeFileName, Is.EqualTo("missing.txt"));
                Assert.That(nativeApi.Placeholders[0].FileSizeBytes, Is.EqualTo(request.RemoteFile.SizeBytes));
                Assert.That(nativeApi.Placeholders[0].FileIdentity, Is.EqualTo(persistedIdentity));
                Assert.That(result.PlaceholderIdentity, Is.EqualTo(persistedIdentity));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RegistersStorageProviderSyncRootBeforeNativeSyncRoot()
        {
            List<string> operations = new List<string>();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi { OperationLog = operations };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
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
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
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
        public void CreateFilePlaceholder_InheritsPinnedParentAndHydratesImmediately()
        {
            string root = Path.Combine(_tempDirectory, "root");
            string parentPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            string filePath = Path.GetFullPath(Path.Combine(parentPath, "remote.txt"));
            Directory.CreateDirectory(parentPath);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                HydrateAction = path => File.WriteAllBytes(path, new byte[12]),
            };
            RecordingShellChangeNotifier shellChangeNotifier = new RecordingShellChangeNotifier();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChangeNotifier,
                readFileAttributes: path => string.Equals(
                        Path.GetFullPath(path),
                        parentPath,
                        StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | (FileAttributes)0x00080000
                    : File.GetAttributes(path));

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(
                CreateRequest(root, "Projects/remote.txt"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { filePath }));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(filePath));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Inherit));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { filePath }));
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[] { "native-hydrate", "native-set-pin-state", "native-set-in-sync-state" }));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(result.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(shellChangeNotifier.ItemUpdates, Is.EqualTo(new[] { filePath }));
            });
        }

        [Test]
        public void CreateFilePlaceholder_PreservesInheritedPinWhenImmediateHydrationIsDeferred()
        {
            const int cloudFileUnsuccessful = unchecked((int)0x80070185);
            string root = Path.Combine(_tempDirectory, "root");
            string parentPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            string filePath = Path.GetFullPath(Path.Combine(parentPath, "remote.txt"));
            Directory.CreateDirectory(parentPath);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                HydrateAction = _ => throw new WindowsCloudFilesNativeException(
                    "CfHydratePlaceholder",
                    cloudFileUnsuccessful),
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                readFileAttributes: path => string.Equals(
                        Path.GetFullPath(path),
                        parentPath,
                        StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | (FileAttributes)0x00080000
                    : File.GetAttributes(path));

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(
                CreateRequest(root, "Projects/remote.txt"));

            WindowsCloudFilesDiagnosticEvent deferred = diagnostics.Snapshot()
                .Single(item => item.Operation == "hydrate-placeholder" && item.Status == "deferred");
            Assert.Multiple(() =>
            {
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { filePath }));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(filePath));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Inherit));
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[] { "native-hydrate", "native-set-pin-state" }));
                Assert.That(deferred.RelativePath, Is.EqualTo("Projects/remote.txt"));
                Assert.That(deferred.HResult, Is.EqualTo(cloudFileUnsuccessful));
            });
        }

        [Test]
        public void CreateDirectoryPlaceholder_CreatesRemoteDirectoryPlaceholderWithoutConversion()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
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
        public void CreateDirectoryPlaceholder_InheritsPinnedParent()
        {
            string root = Path.Combine(_tempDirectory, "root");
            string parentPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            string directoryPath = Path.GetFullPath(Path.Combine(parentPath, "Nested"));
            Directory.CreateDirectory(parentPath);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: _ => false,
                readFileAttributes: path => string.Equals(
                        Path.GetFullPath(path),
                        parentPath,
                        StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | (FileAttributes)0x00080000
                    : File.GetAttributes(path));

            adapter.CreateDirectoryPlaceholder(CreateDirectoryRequest(root, "Projects/Nested"));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(directoryPath));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Inherit));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
            });
        }

        [Test]
        public void CreateDirectoryPlaceholder_ConvertsNonEmptyExistingDirectoryToCloudFilesPlaceholder()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
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
        public void CreateDirectoryPlaceholder_RepairsExistingCloudFilesDirectoryPlaceholderAndPreservesPinnedState()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(directoryPath);
            RemoteDirectoryMaterializationRequest request = CreateDirectoryRequest(root, "Projects");
            TrackExistingDirectoryPlaceholder(nativeApi, directoryPath, request);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => FileAttributes.Directory
                    | FileAttributes.ReparsePoint
                    | (FileAttributes)0x00080000);

            adapter.CreateDirectoryPlaceholder(request);

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
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates[0].FilePath, Is.EqualTo(directoryPath));
                Assert.That(nativeApi.PinStates[0].PinState, Is.EqualTo(WindowsCloudFilesPinState.Pinned));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { directoryPath }));
                Assert.That(nativeApi.CallLog, Is.EqualTo(new[] { "native-update", "native-set-pin-state", "native-set-in-sync-state" }));
                Assert.That(identity.RelativePath, Is.EqualTo("Projects"));
                Assert.That(identity.NodeId, Is.EqualTo(Guid.Parse("88888888-8888-8888-8888-888888888888")));
                Assert.That(events.Any(static item => item is { Operation: "convert-directory-placeholder", Status: "repaired-placeholder" }), Is.True);
            });
        }
    }
}
