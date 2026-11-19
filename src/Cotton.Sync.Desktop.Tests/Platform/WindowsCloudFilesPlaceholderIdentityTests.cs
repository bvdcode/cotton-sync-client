// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsCloudFilesPlaceholderIdentityTests
    {
        [TestCase(false, "Projects/report.zip", "Projects/report.zip", true)]
        [TestCase(false, "Projects/report.zip", "Archive/report.zip", true)]
        [TestCase(true, "Projects/report.zip", "Projects/report.zip", true)]
        [TestCase(true, "Projects/report.zip", "projects/REPORT.zip", true)]
        [TestCase(true, "Projects/report.zip", "Archive/report.zip", false)]
        public void CanRefresh_RecognizesRemoteReplacementAndPreservesMoveOwnership(
            bool replaced, string previousPath, string currentPath, bool expected)
        {
            Guid pairId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            Guid originalId = Guid.NewGuid();
            RemoteFilePlaceholderRequest previous = new(pairId.ToString("D"), "root", rootId,
                previousPath, new NodeFileManifestDto { Id = originalId });
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Create(previous, previousPath);
            RemoteFilePlaceholderRequest current = previous with
            {
                RelativePath = currentPath,
                RemoteFile = new NodeFileManifestDto { Id = replaced ? Guid.NewGuid() : originalId },
            };

            Assert.That(identity.CanRefresh(current), Is.EqualTo(expected));
        }

        [TestCase("pair")]
        [TestCase("root")]
        public void CanRefresh_RejectsReplacementFromAnotherSyncRoot(string foreignIdentity)
        {
            RemoteFilePlaceholderRequest request = new(Guid.NewGuid().ToString("D"), "root", Guid.NewGuid(),
                "Projects/report.zip", new NodeFileManifestDto { Id = Guid.NewGuid() });
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Create(request, request.RelativePath);
            identity = foreignIdentity switch
            {
                "pair" => identity with { SyncPairId = Guid.NewGuid() },
                "root" => identity with { RemoteRootNodeId = Guid.NewGuid() },
                _ => throw new ArgumentOutOfRangeException(nameof(foreignIdentity)),
            };
            request = request with { RemoteFile = new NodeFileManifestDto { Id = Guid.NewGuid() } };

            Assert.That(identity.CanRefresh(request), Is.False);
        }
    }
}
