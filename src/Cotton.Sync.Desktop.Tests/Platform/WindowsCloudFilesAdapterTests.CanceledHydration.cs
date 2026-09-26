// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public void HydratePlaceholder_DoesNotRestorePinRemovedDuringDownload()
        {
            FileAttributes attributes = FileAttributes.ReparsePoint | (FileAttributes)0x00080000;
            FakeCloudFilesNativeApi nativeApi = new()
            {
                HydrateAction = _ => attributes = FileAttributes.ReparsePoint,
            };
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(), nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => attributes);

            adapter.HydratePlaceholder(CreateSyncPair(root), "Projects/remote-only.txt");

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { target }));
                Assert.That(nativeApi.PinStates, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
            });
        }
    }
}
