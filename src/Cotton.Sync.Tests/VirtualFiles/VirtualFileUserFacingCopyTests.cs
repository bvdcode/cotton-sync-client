// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests.VirtualFiles
{
    public class VirtualFileUserFacingCopyTests
    {
        [TestCase(SyncPlaceholderHydrationState.RemoteOnly, "Online-only", "Visible in File Explorer and downloads when opened.")]
        [TestCase(SyncPlaceholderHydrationState.Hydrated, "Available on this device", "Content is stored locally and stays synced.")]
        [TestCase(SyncPlaceholderHydrationState.Dehydrated, "Freed from this device", "Visible in File Explorer and downloads again when opened.")]
        [TestCase(SyncPlaceholderHydrationState.HydrationFailed, "Needs attention", "Cotton Sync needs your review before virtual files can continue.")]
        [TestCase(SyncPlaceholderHydrationState.None, "Not a virtual file", "This item is not tracked as a Windows virtual file.")]
        public void HydrationStateCopy_UsesUserFacingExplorerTerms(
            SyncPlaceholderHydrationState state,
            string expectedLabel,
            string expectedDescription)
        {
            Assert.Multiple(() =>
            {
                Assert.That(VirtualFileUserFacingCopy.GetHydrationStateLabel(state), Is.EqualTo(expectedLabel));
                Assert.That(VirtualFileUserFacingCopy.GetHydrationStateDescription(state), Is.EqualTo(expectedDescription));
            });
        }

        [Test]
        public void ProgressAndActionCopy_AvoidsPlaceholderTerminology()
        {
            Assert.Multiple(() =>
            {
                Assert.That(VirtualFileUserFacingCopy.CreatingCloudFilesProgressLabel, Is.EqualTo("Making cloud files available"));
                Assert.That(VirtualFileUserFacingCopy.PreparingCloudFilesProgressLabel, Is.EqualTo("Preparing cloud files"));
                Assert.That(VirtualFileUserFacingCopy.CloudFileAvailableActivityVerb, Is.EqualTo("Made cloud file available"));
                Assert.That(VirtualFileUserFacingCopy.CloudFileAvailableActivityVerb, Does.Not.Contain("placeholder"));
                Assert.That(VirtualFileUserFacingCopy.CloudFilesProgressUnit, Is.EqualTo("cloud files"));
                Assert.That(VirtualFileUserFacingCopy.RemoteOnlyLocalChangeRequiresActionMessage, Does.Contain("online-only file"));
                Assert.That(VirtualFileUserFacingCopy.RemoteOnlyLocalChangeRequiresActionMessage, Does.Not.Contain("placeholder"));
                Assert.That(VirtualFileUserFacingCopy.CloudFilesPlaceholderFailedMessage, Does.Contain("cloud file"));
                Assert.That(VirtualFileUserFacingCopy.CloudFilesPlaceholderFailedMessage, Does.Not.Contain("placeholder"));
            });
        }
    }
}
