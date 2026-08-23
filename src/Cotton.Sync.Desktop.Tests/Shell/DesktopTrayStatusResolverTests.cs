// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public class DesktopTrayStatusResolverTests
    {
        [Test]
        public void FromShellState_ReturnsSignedOutWhenSessionIsMissing()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: false,
                statusText: "Connected",
                hasStatusAttention: false);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.SignedOut));
                Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Signed out"));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/tray-signed-out.png"));
            });
        }

        [Test]
        public void FromShellState_ReturnsErrorWhenActionIsRequired()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Connected",
                hasStatusAttention: true);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Error));
                Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Action required"));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/tray-error.png"));
            });
        }

        [Test]
        public void FromShellState_ReturnsErrorWhenConflictsNeedReview()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Conflicts need review",
                hasStatusAttention: true);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Error));
                Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Conflicts need review"));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/tray-error.png"));
            });
        }

        [Test]
        public void FromShellState_ReturnsOfflineWhenGlobalStatusIsOffline()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Offline",
                hasStatusAttention: false);

            Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Offline));
        }

        [Test]
        public void FromShellState_ReturnsPausedWhenGlobalStatusIsPaused()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Paused",
                hasStatusAttention: false);

            Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Paused));
        }

        [Test]
        public void FromShellState_ReturnsIdleWhenSyncTextHasNoActiveProgress()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Sync requested",
                hasStatusAttention: false);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Idle));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/icon-192.png"));
            });
        }

        [Test]
        public void FromShellState_ReturnsSyncingWhenWorkProgressIsActive()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Connected",
                hasStatusAttention: false,
                hasActiveSyncProgress: true);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Syncing));
                Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Syncing"));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/tray-syncing.png"));
            });
        }

        [Test]
        public void FromShellState_AddsActiveProgressToSyncingTooltip()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Connected",
                hasStatusAttention: false,
                hasActiveSyncProgress: true,
                activeProgressTitle: "Syncing 2 folders",
                activeProgressDetails: "10 of 40 files across 2 folders",
                activeProgressHeaderDetails: "6.0 MB / 24 MB · 3.0 MB/s · 6s left",
                activeActivityKind: DesktopTrayActivityKind.Uploading);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Uploading));
                Assert.That(
                    status.ToolTipText,
                    Is.EqualTo(
                        "Cotton Sync - Syncing 2 folders - 10 of 40 files across 2 folders - "
                        + "6.0 MB / 24 MB · 3.0 MB/s · 6s left"));
                Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/tray-uploading.png"));
            });
        }

        [Test]
        public void FromShellState_UsesRunProgressDetailsInSyncingTooltipWhenHeaderDetailsAreMissing()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Connected",
                hasStatusAttention: false,
                hasActiveSyncProgress: true,
                activeProgressTitle: "Documents: Scanning local files",
                activeProgressDetails: "123 files found · report.txt");

            Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Documents: Scanning local files - 123 files found · report.txt"));
        }

        [Test]
        public void FromShellState_UsesActionSpecificActiveIcon()
        {
            (DesktopTrayActivityKind ActivityKind, DesktopTrayStatusKind StatusKind, string AssetName)[] cases =
            [
                (DesktopTrayActivityKind.Downloading, DesktopTrayStatusKind.Downloading, "tray-downloading.png"),
                (DesktopTrayActivityKind.MakingAvailable, DesktopTrayStatusKind.Downloading, "tray-downloading.png"),
                (DesktopTrayActivityKind.FreeingSpace, DesktopTrayStatusKind.FreeingSpace, "tray-freeing-space.png"),
            ];
            foreach ((DesktopTrayActivityKind activityKind, DesktopTrayStatusKind statusKind, string assetName) in cases)
            {
                DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                    isSignedIn: true,
                    statusText: "Syncing",
                    hasStatusAttention: false,
                    hasActiveSyncProgress: true,
                    activeProgressTitle: "Music",
                    activeProgressDetails: "Making files available · 10 of 40 files",
                    activeActivityKind: activityKind);

                Assert.Multiple(() =>
                {
                    Assert.That(status.Kind, Is.EqualTo(statusKind));
                    Assert.That(status.IconUri.ToString(), Does.EndWith("/Assets/" + assetName));
                });
            }
        }

        [Test]
        public void FromShellState_PrioritizesActionAndBoundsLongTooltip()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Syncing",
                hasStatusAttention: false,
                hasActiveSyncProgress: true,
                activeProgressTitle: "A very long sync folder title that still needs to be recognizable",
                activeProgressDetails: "Making files available · 100 of 1000 files · Local change · 1 changed path",
                activeProgressHeaderDetails: "6.0 GB / 24 GB · 30 MB/s · 6m left",
                activeActivityKind: DesktopTrayActivityKind.MakingAvailable);

            Assert.Multiple(() =>
            {
                Assert.That(status.ToolTipText.Length, Is.EqualTo(127));
                Assert.That(status.ToolTipText, Does.Contain("Making files available"));
                Assert.That(status.ToolTipText, Does.EndWith("..."));
            });
        }

        [Test]
        public void FromShellState_ReturnsIdleWhenSignedInAndNoStatusMatches()
        {
            DesktopTrayStatus status = DesktopTrayStatusResolver.FromShellState(
                isSignedIn: true,
                statusText: "Connected",
                hasStatusAttention: false);

            Assert.Multiple(() =>
            {
                Assert.That(status.Kind, Is.EqualTo(DesktopTrayStatusKind.Idle));
                Assert.That(status.ToolTipText, Is.EqualTo("Cotton Sync - Connected"));
            });
        }

        [Test]
        public void Resolve_ReturnsPlainCottonIconForIdleState()
        {
            Uri iconUri = DesktopTrayIconAssetResolver.Resolve(DesktopTrayStatusKind.Idle);

            Assert.That(iconUri.ToString(), Does.EndWith("/Assets/icon-192.png"));
        }

        [Test]
        public void Resolve_ReturnsDedicatedIconForNonIdleTrayStates()
        {
            (DesktopTrayStatusKind Kind, string AssetName)[] cases =
            [
                (DesktopTrayStatusKind.SignedOut, "tray-signed-out.png"),
                (DesktopTrayStatusKind.Syncing, "tray-syncing.png"),
                (DesktopTrayStatusKind.Paused, "tray-paused.png"),
                (DesktopTrayStatusKind.Offline, "tray-offline.png"),
                (DesktopTrayStatusKind.Error, "tray-error.png"),
                (DesktopTrayStatusKind.Uploading, "tray-uploading.png"),
                (DesktopTrayStatusKind.Downloading, "tray-downloading.png"),
                (DesktopTrayStatusKind.FreeingSpace, "tray-freeing-space.png"),
            ];

            foreach ((DesktopTrayStatusKind kind, string assetName) in cases)
            {
                Uri iconUri = DesktopTrayIconAssetResolver.Resolve(kind);

                Assert.That(iconUri.ToString(), Does.EndWith("/Assets/" + assetName));
            }
        }

        [Test]
        public void Resolve_RejectsUnknownState()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DesktopTrayIconAssetResolver.Resolve(DesktopTrayStatusKind.Unknown));
        }

        [Test]
        public void TaskbarOverlayResolve_ClearsIdleAndSignedOutStates()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DesktopTaskbarOverlayIconAssetResolver.Resolve(DesktopTrayStatusKind.Idle), Is.Null);
                Assert.That(DesktopTaskbarOverlayIconAssetResolver.Resolve(DesktopTrayStatusKind.SignedOut), Is.Null);
            });
        }

        [Test]
        public void TaskbarOverlayResolve_ReturnsDedicatedActivityAssets()
        {
            (DesktopTrayStatusKind Kind, string AssetName)[] cases =
            [
                (DesktopTrayStatusKind.Syncing, "taskbar-syncing.ico"),
                (DesktopTrayStatusKind.Paused, "taskbar-paused.ico"),
                (DesktopTrayStatusKind.Offline, "taskbar-offline.ico"),
                (DesktopTrayStatusKind.Error, "taskbar-error.ico"),
                (DesktopTrayStatusKind.Uploading, "taskbar-uploading.ico"),
                (DesktopTrayStatusKind.Downloading, "taskbar-downloading.ico"),
                (DesktopTrayStatusKind.FreeingSpace, "taskbar-freeing-space.ico"),
            ];

            foreach ((DesktopTrayStatusKind kind, string assetName) in cases)
            {
                string? iconPath = DesktopTaskbarOverlayIconAssetResolver.Resolve(kind);

                Assert.That(iconPath, Does.EndWith(Path.Combine("Assets", assetName)));
            }
        }

        [Test]
        public void TaskbarOverlayResolve_RejectsUnknownState()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DesktopTaskbarOverlayIconAssetResolver.Resolve(DesktopTrayStatusKind.Unknown));
        }
    }
}
