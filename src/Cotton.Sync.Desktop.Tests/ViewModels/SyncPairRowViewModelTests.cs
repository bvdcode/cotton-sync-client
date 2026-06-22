// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.ViewModels;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public class SyncPairRowViewModelTests
    {
        [Test]
        public void StatusIndicator_UsesBrandDotForEnabledIdleAndClassifiesVisibleStates()
        {
            var row = new SyncPairRowViewModel
            {
                DisplayName = "Videos",
                Status = " idle ",
            };

            Assert.Multiple(() =>
            {
                Assert.That(row.DisplayStatus, Is.Empty);
                Assert.That(row.HasDisplayStatus, Is.False);
                Assert.That(row.IsStatusIndicatorVisible, Is.True);
                Assert.That(row.IsStatusActive, Is.True);
                Assert.That(row.StatusIndicatorToolTip, Is.EqualTo("Sync enabled"));
            });

            row.Status = " Syncing ";

            Assert.Multiple(() =>
            {
                Assert.That(row.DisplayStatus, Is.EqualTo("Syncing"));
                Assert.That(row.HasDisplayStatus, Is.True);
                Assert.That(row.IsStatusIndicatorVisible, Is.True);
                Assert.That(row.IsStatusActive, Is.True);
                Assert.That(row.IsStatusPaused, Is.False);
                Assert.That(row.IsStatusAttention, Is.False);
            });

            row.Status = "Paused";

            Assert.Multiple(() =>
            {
                Assert.That(row.DisplayStatus, Is.EqualTo("Paused"));
                Assert.That(row.IsStatusActive, Is.False);
                Assert.That(row.IsStatusPaused, Is.True);
                Assert.That(row.IsStatusOffline, Is.False);
                Assert.That(row.IsStatusAttention, Is.False);
            });

            row.Status = "Offline";

            Assert.Multiple(() =>
            {
                Assert.That(row.DisplayStatus, Is.EqualTo("Offline"));
                Assert.That(row.IsStatusActive, Is.False);
                Assert.That(row.IsStatusPaused, Is.False);
                Assert.That(row.IsStatusOffline, Is.True);
                Assert.That(row.IsStatusAttention, Is.False);
            });

            row.Status = "Error";

            Assert.Multiple(() =>
            {
                Assert.That(row.DisplayStatus, Is.EqualTo("Error"));
                Assert.That(row.IsStatusOffline, Is.False);
                Assert.That(row.IsStatusAttention, Is.True);
            });
        }

        [Test]
        public void ModeLabel_DescribesMaterializationMode()
        {
            var row = new SyncPairRowViewModel();

            Assert.Multiple(() =>
            {
                Assert.That(row.ModeLabel, Is.EqualTo("Full mirror"));
                Assert.That(row.IsFullMirrorMode, Is.True);
                Assert.That(row.IsWindowsVirtualFilesMode, Is.False);
            });

            row.Mode = SyncPairMode.WindowsVirtualFiles;

            Assert.Multiple(() =>
            {
                Assert.That(row.ModeLabel, Is.EqualTo("Windows virtual files"));
                Assert.That(row.IsFullMirrorMode, Is.False);
                Assert.That(row.IsWindowsVirtualFilesMode, Is.True);
            });
        }

        [Test]
        public void RemotePathLabel_HidesRootPathNoise()
        {
            var row = new SyncPairRowViewModel
            {
                RemotePath = "/",
            };

            Assert.Multiple(() =>
            {
                Assert.That(row.RemotePathLabel, Is.Empty);
                Assert.That(row.HasRemotePathLabel, Is.False);
            });

            row.RemotePath = "/Documents";

            Assert.Multiple(() =>
            {
                Assert.That(row.RemotePathLabel, Is.EqualTo("/Documents"));
                Assert.That(row.HasRemotePathLabel, Is.True);
            });
        }
    }
}
