// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [TestCase("Projects/report.txt", "Projects/Archive/report.txt")]
        [TestCase("Projects/report.txt", "Projects/renamed.txt")]
        public void CreateFilePlaceholder_AfterMoveRefreshesPathForSameRemoteFile(string oldPath, string newPath)
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, newPath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            TrackExistingFilePlaceholder(nativeApi, target, CreateRequest(root, oldPath));
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi,
                isReparsePoint: path => path == target,
                isCloudFilesReparsePoint: path => path == target);

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(CreateRequest(root, newPath));

            WindowsCloudFilesPlaceholderIdentity identity =
                WindowsCloudFilesPlaceholderIdentity.Parse(result.PlaceholderIdentity!);
            Assert.Multiple(() =>
            {
                Assert.That(identity.RelativePath, Is.EqualTo(newPath));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [TestCase("Projects", "Archive/Projects")]
        [TestCase("Projects", "Renamed")]
        public void CreateDirectoryPlaceholder_AfterMoveRefreshesPathForSameRemoteDirectory(string oldPath, string newPath)
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, newPath));
            Directory.CreateDirectory(target);
            TrackExistingDirectoryPlaceholder(nativeApi, target, CreateDirectoryRequest(root, oldPath));
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi,
                isReparsePoint: path => path == target,
                isCloudFilesReparsePoint: path => path == target);

            adapter.CreateDirectoryPlaceholder(CreateDirectoryRequest(root, newPath));

            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                WindowsCloudFilesDirectoryPlaceholderIdentity.Parse(nativeApi.UpdatedPlaceholders.Single().FileIdentity);
            Assert.Multiple(() =>
            {
                Assert.That(identity.RelativePath, Is.EqualTo(newPath));
                Assert.That(nativeApi.Placeholders, Is.Empty);
                Assert.That(nativeApi.ConvertedPlaceholders, Is.Empty);
            });
        }

        [TestCase("pair")]
        [TestCase("root")]
        [TestCase("item")]
        public void CreateFilePlaceholder_AfterMoveRejectsUnrelatedIdentity(string changedIdentity)
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Archive", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Archive/report.txt");
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Create(request, "Projects/report.txt");
            identity = changedIdentity switch
            {
                "pair" => identity with { SyncPairId = Guid.NewGuid() },
                "root" => identity with { RemoteRootNodeId = Guid.NewGuid() },
                "item" => identity with { NodeFileId = Guid.NewGuid() },
                _ => throw new ArgumentOutOfRangeException(nameof(changedIdentity)),
            };
            nativeApi.PlaceholderIdentities[target] = identity.ToBytes();
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi,
                isReparsePoint: path => path == target,
                isCloudFilesReparsePoint: path => path == target);

            Assert.Throws<RemoteFilePlaceholderUnavailableException>(() => adapter.CreateFilePlaceholder(request));
            Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
        }

        [TestCase("pair")]
        [TestCase("root")]
        [TestCase("item")]
        public void CreateDirectoryPlaceholder_AfterMoveRejectsUnrelatedIdentity(string changedIdentity)
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Archive", "Projects"));
            Directory.CreateDirectory(target);
            RemoteDirectoryMaterializationRequest request = CreateDirectoryRequest(root, "Archive/Projects");
            WindowsCloudFilesDirectoryPlaceholderIdentity identity =
                WindowsCloudFilesDirectoryPlaceholderIdentity.Create(request, "Projects");
            identity = changedIdentity switch
            {
                "pair" => identity with { SyncPairId = Guid.NewGuid() },
                "root" => identity with { RemoteRootNodeId = Guid.NewGuid() },
                "item" => identity with { NodeId = Guid.NewGuid() },
                _ => throw new ArgumentOutOfRangeException(nameof(changedIdentity)),
            };
            nativeApi.PlaceholderIdentities[target] = identity.ToBytes();
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi,
                isReparsePoint: path => path == target,
                isCloudFilesReparsePoint: path => path == target);

            Assert.Throws<InvalidOperationException>(() => adapter.CreateDirectoryPlaceholder(request));
            Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
        }
    }
}
