// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class DesktopCloudFilesPlaceholderWriterTests
    {
        [TestCase(unchecked((int)0x80070020), false)]
        [TestCase(unchecked((int)0x80070021), false)]
        [TestCase(unchecked((int)0x80070020), true)]
        [TestCase(unchecked((int)0x80070021), true)]
        public async Task CreatePlaceholderAsync_WhenFileIsBusyWaitsForExclusiveAccessAndCanRetry(int hResult, bool batch)
        {
            WindowsCloudFilesNativeException nativeFailure = new("CfOpenFileWithOplock", hResult);
            FakeCloudFilesAdapter adapter = new() { Exception = nativeFailure };
            DesktopCloudFilesPlaceholderWriter writer = new(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new(true, "Cloud Files available."));
            RemoteFilePlaceholderRequest request = CreateRequest(_tempDirectory);

            async Task CreateAsync()
            {
                if (batch)
                {
                    await writer.CreatePlaceholdersAsync([request]);
                }
                else
                {
                    await writer.CreatePlaceholderAsync(request);
                }
            }

            LocalFileUnavailableException? failure = Assert.ThrowsAsync<LocalFileUnavailableException>(CreateAsync);
            Assert.Multiple(() =>
            {
                Assert.That(failure?.RelativePath, Is.EqualTo(request.RelativePath));
                Assert.That(failure?.FullPath, Is.EqualTo(Path.Combine(_tempDirectory, request.RelativePath)));
                Assert.That(failure?.RequiresExclusiveAccess, Is.True);
                Assert.That(failure?.InnerException, Is.SameAs(nativeFailure));
            });

            adapter.Exception = null;
            await CreateAsync();
            Assert.That(adapter.Requests.Last(), Is.SameAs(request));
        }
    }
}
