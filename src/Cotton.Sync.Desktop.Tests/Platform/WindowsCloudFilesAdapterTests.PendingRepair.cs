// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public void CreateDirectoryPlaceholder_LeavesPendingDirectoryUnchanged()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            string root = Path.Combine(_tempDirectory, "root");
            string directoryPath = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(directoryPath);
            RemoteDirectoryMaterializationRequest request = CreateDirectoryRequest(root, "Projects");
            TrackExistingDirectoryPlaceholder(nativeApi, directoryPath, request);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi, diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(Path.GetFullPath(path), directoryPath, StringComparison.OrdinalIgnoreCase));

            adapter.CreateDirectoryPlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
                Assert.That(nativeApi.PinStates, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(diagnostics.Snapshot().Any(static item => item is
                    { Operation: "convert-directory-placeholder", Status: "skipped-pending" }), Is.True);
            });
        }
    }
}
