// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Cotton.Files;
using Cotton.Nodes;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class DesktopCloudFilesPlaceholderWriterTests
    {
        [Test]
        public async Task BeforeCreateDirectoryAsync_SuppressesLocalWatcherEventsForDirectoryPath()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                localChangeSuppression: suppression,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            Guid remoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            await writer.BeforeCreateDirectoryAsync(new RemoteDirectoryMaterializationRequest(
                syncPairId.ToString("D"),
                _tempDirectory,
                remoteRootNodeId,
                "Projects/Nested",
                new NodeDto { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Nested" }));

            Assert.Multiple(() =>
            {
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPairId, _tempDirectory, "Projects/Nested") }));
                Assert.That(adapter.DirectoryPlaceholders, Has.Count.EqualTo(1));
                Assert.That(adapter.DirectoryPlaceholders[0].RemoteRootNodeId, Is.EqualTo(remoteRootNodeId));
                Assert.That(adapter.DirectoryPlaceholders[0].RelativePath, Is.EqualTo("Projects/Nested"));
            });
        }

        [Test]
        public async Task BeforeWriteFileAsync_SuppressesProviderMaterializationWithRemoteBaseline()
        {
            RecordingLocalChangeSuppression suppression = new();
            DesktopCloudFilesPlaceholderWriter writer = new(
                localChangeSuppression: suppression,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            RemoteFilePlaceholderRequest placeholderRequest = CreateRequest(
                _tempDirectory,
                syncPairId.ToString("D"),
                "Projects/report (Cotton conflict 20260803T200000Z).txt");

            await writer.BeforeWriteFileAsync(new RemoteFileMaterializationRequest(
                placeholderRequest.SyncPairId,
                placeholderRequest.LocalRootPath,
                placeholderRequest.RemoteRootNodeId,
                placeholderRequest.RelativePath,
                placeholderRequest.RemoteFile));

            Assert.Multiple(() =>
            {
                Assert.That(
                    suppression.SuppressedFileMaterializations,
                    Is.EqualTo(new[]
                    {
                        new SuppressedFileMaterialization(
                            syncPairId,
                            _tempDirectory,
                            placeholderRequest.RelativePath,
                            placeholderRequest.RemoteFile.SizeBytes,
                            placeholderRequest.RemoteFile.UpdatedAt),
                    }));
                Assert.That(suppression.SuppressedFileCreations, Is.Empty);
                Assert.That(suppression.SuppressedWrites, Is.Empty);
            });
        }

        [Test]
        public async Task AfterWriteFileAsync_PersistsProviderCreatedFileMarker()
        {
            RecordingProviderFileMarker marker = new RecordingProviderFileMarker();
            DesktopCloudFilesPlaceholderWriter writer = new(
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."),
                providerFileMarker: marker);
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            RemoteFilePlaceholderRequest placeholderRequest = CreateRequest(
                _tempDirectory,
                syncPairId.ToString("D"),
                "Projects/report (Cotton conflict 20260803T200000Z).txt");

            await writer.AfterWriteFileAsync(new RemoteFileMaterializationRequest(
                placeholderRequest.SyncPairId,
                placeholderRequest.LocalRootPath,
                placeholderRequest.RemoteRootNodeId,
                placeholderRequest.RelativePath,
                placeholderRequest.RemoteFile));

            Assert.Multiple(() =>
            {
                Assert.That(marker.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(marker.LocalRootPath, Is.EqualTo(_tempDirectory));
                Assert.That(marker.RelativePath, Is.EqualTo(placeholderRequest.RelativePath));
                Assert.That(marker.ContentHash, Is.EqualTo(placeholderRequest.RemoteFile.ContentHash));
                Assert.That(marker.SizeBytes, Is.EqualTo(placeholderRequest.RemoteFile.SizeBytes));
            });
        }

        [Test]
        public async Task AfterCreateDirectoryAsync_EnsuresDirectoryPlaceholderThroughAdapter()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            Guid remoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            await writer.AfterCreateDirectoryAsync(new RemoteDirectoryMaterializationRequest(
                syncPairId.ToString("D"),
                _tempDirectory,
                remoteRootNodeId,
                "Projects/Nested",
                new NodeDto { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Nested" }));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.DirectoryPlaceholders, Has.Count.EqualTo(1));
                Assert.That(adapter.DirectoryPlaceholders[0].SyncPairId, Is.EqualTo(syncPairId.ToString("D")));
                Assert.That(adapter.DirectoryPlaceholders[0].LocalRootPath, Is.EqualTo(_tempDirectory));
                Assert.That(adapter.DirectoryPlaceholders[0].RemoteRootNodeId, Is.EqualTo(remoteRootNodeId));
                Assert.That(adapter.DirectoryPlaceholders[0].RelativePath, Is.EqualTo("Projects/Nested"));
                Assert.That(adapter.DirectoryPlaceholders[0].RemoteDirectory.Id, Is.EqualTo(Guid.Parse("22222222-2222-2222-2222-222222222222")));
            });
        }

        [Test]
        public async Task AfterDirectoryTreePopulationAsync_MarksChildrenBeforeParentsInSync()
        {
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            DesktopCloudFilesPlaceholderWriter writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                localChangeSuppression: suppression,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            Guid syncPairId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            Guid remoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            await writer.AfterDirectoryTreePopulationAsync(
            [
                CreateDirectoryRequest(syncPairId, remoteRootNodeId, "Projects"),
                CreateDirectoryRequest(syncPairId, remoteRootNodeId, "Projects/Nested"),
                CreateDirectoryRequest(syncPairId, remoteRootNodeId, "Projects"),
            ]);

            Assert.Multiple(() =>
            {
                Assert.That(
                    adapter.DirectoryPlaceholders.Select(static state => state.RelativePath),
                    Is.EqualTo(new[] { "Projects/Nested", "Projects" }));
                Assert.That(
                    adapter.SyncRootInSyncPairs.Select(static state => state.Id),
                    Is.EqualTo(new[] { syncPairId }));
                Assert.That(adapter.SyncRootInSyncPairs[0].LocalRootPath, Is.EqualTo(_tempDirectory));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPairId, _tempDirectory, "Projects/Nested"),
                        new SuppressedWrite(syncPairId, _tempDirectory, "Projects"),
                    }));
            });
        }

    }
}
