// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public class LocalChangeSuppressionAvailabilityTests
    {
        [Test]
        public void ProviderPinnedWriteSuppression_EndsImmediatelyWhenUserUnpins()
        {
            bool pinned = true;
            Guid syncPairId = Guid.NewGuid();
            string rootPath = Path.Combine(Path.GetTempPath(), "cotton-pinned-suppression");
            string fullPath = Path.Combine(rootPath, "Music", "track.mp3");
            LocalChangeSuppression suppression = new LocalChangeSuppression(
                _ => false,
                pinnedCloudFilesPlaceholderProbe: _ => pinned);
            suppression.SuppressProviderPinnedWrite(syncPairId, rootPath, "Music/track.mp3");
            LocalSyncRootChange change = new LocalSyncRootChange(
                syncPairId,
                fullPath,
                LocalSyncRootChangeKind.AttributesChanged);

            bool providerEchoSuppressed = suppression.ShouldSuppress(change);
            pinned = false;
            bool userUnpinSuppressed = suppression.ShouldSuppress(change);

            Assert.Multiple(() =>
            {
                Assert.That(providerEchoSuppressed, Is.True);
                Assert.That(userUnpinSuppressed, Is.False);
            });
        }

        [Test]
        public void ProviderOnlineOnlyWriteSuppression_EndsImmediatelyWhenUserPins()
        {
            bool onlineOnly = true;
            Guid syncPairId = Guid.NewGuid();
            string rootPath = Path.Combine(Path.GetTempPath(), "cotton-online-only-suppression");
            string fullPath = Path.Combine(rootPath, "Music", "track.mp3");
            LocalChangeSuppression suppression = new LocalChangeSuppression(_ => onlineOnly);
            suppression.SuppressProviderOnlineOnlyWrite(syncPairId, rootPath, "Music/track.mp3");
            LocalSyncRootChange change = new LocalSyncRootChange(
                syncPairId,
                fullPath,
                LocalSyncRootChangeKind.AttributesChanged);

            bool providerEchoSuppressed = suppression.ShouldSuppress(change);
            onlineOnly = false;
            bool userPinSuppressed = suppression.ShouldSuppress(change);

            Assert.Multiple(() =>
            {
                Assert.That(providerEchoSuppressed, Is.True);
                Assert.That(userPinSuppressed, Is.False);
            });
        }
    }
}
