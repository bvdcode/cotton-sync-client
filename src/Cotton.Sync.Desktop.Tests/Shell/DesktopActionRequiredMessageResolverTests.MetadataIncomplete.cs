// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopActionRequiredMessageResolverTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CloudFilesFailure_RetainsCauseAndEveryPathInDashboardAndUsesCauseInNotification(bool dehydration)
        {
            Guid id = Guid.NewGuid();
            string path = "Lessons/" + new string('a', 200) + "/Видео.mp4";
            string message = dehydration
                ? VirtualFileUserFacingCopy.CreateDehydrationIncompleteMessage([path, "Lessons/second.mp4"])
                : VirtualFileUserFacingCopy.CreateMetadataIncompleteMessage([path, "Lessons/second.mp4"]);
            string title = dehydration ? "Free up space incomplete" : "Sync incomplete";
            DesktopSyncStatusSnapshot status = new([new(id, "Error", message)]);
            DesktopNotificationTracker tracker = new();
            DesktopNotificationRequest notification = tracker.Apply(status, new Dictionary<Guid, string> { [id] = "Cloud" }).Single();

            Assert.Multiple(() =>
            {
                Assert.That(DesktopActionRequiredMessageResolver.FromStatus(status), Is.EqualTo(message));
                Assert.That(DesktopActionRequiredMessageResolver.FromException(new SyncActionRequiredException(message)), Is.EqualTo(message));
                Assert.That(notification.Title, Is.EqualTo(title));
                Assert.That(notification.Message, Does.Contain("0x80070299"));
                Assert.That(notification.Message.Length, Is.LessThanOrEqualTo(DesktopUserMessageFormatter.DefaultMaxLength));
            });
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void CloudFilesFailure_DoesNotHideAnotherIssueRequiringApproval(bool samePair, bool dehydration)
        {
            const string approval = "Remote delete blocked by mass-delete guard. 238 pending deletes exceed limit 100.";
            string metadata = dehydration
                ? VirtualFileUserFacingCopy.CreateDehydrationIncompleteMessage(["Lessons/video.mp4"])
                : VirtualFileUserFacingCopy.CreateMetadataIncompleteMessage(["Lessons/video.mp4"]);
            DesktopSyncStatusSnapshot status = samePair
                ? new([new(Guid.NewGuid(), "Error", approval + Environment.NewLine + metadata)])
                : new([new(Guid.NewGuid(), "Error", metadata), new(Guid.NewGuid(), "Error", approval)]);

            string message = DesktopActionRequiredMessageResolver.FromStatus(status);
            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("238"));
                Assert.That(DesktopActionRequiredMessageResolver.GetIssueTitle(message), Is.EqualTo("Action required"));
            });
        }
    }
}
