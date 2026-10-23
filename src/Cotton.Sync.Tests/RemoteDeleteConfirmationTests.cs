// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public class RemoteDeleteConfirmationTests
    {
        [TestCase(SyncChangeKind.FolderRestored)]
        [TestCase(SyncChangeKind.FolderMoved)]
        public void LaterAncestorChangeRevokesEarlierDelete(SyncChangeKind laterKind)
        {
            Guid folderId = Guid.NewGuid();
            Dictionary<string, SyncStateEntry> directories = DirectoryStates(folderId);
            Dictionary<string, SyncStateEntry> files = FileStates();
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
                [Change(1, SyncChangeKind.FolderDeleted, folderId), Change(2, laterKind, folderId)]);

            Assert.That(RemoteDeleteConfirmation.CreatePathKeys(changes, files, directories), Is.Empty);
        }

        [TestCase(SyncChangeKind.FileRestored)]
        [TestCase(SyncChangeKind.FileMoved)]
        [TestCase(SyncChangeKind.FileContentUpdated)]
        public void LaterChildChangeRevokesInheritedDelete(SyncChangeKind laterKind)
        {
            Guid folderId = Guid.NewGuid();
            Dictionary<string, SyncStateEntry> directories = DirectoryStates(folderId);
            Dictionary<string, SyncStateEntry> files = FileStates();
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
                [Change(1, SyncChangeKind.FolderDeleted, folderId),
                    Change(2, laterKind, files["ARCHIVE/FILE.TXT"].RemoteFileId!.Value)]);

            Assert.That(RemoteDeleteConfirmation.CreatePathKeys(changes, files, directories),
                Is.EquivalentTo(new[] { "ARCHIVE" }));
        }

        [Test]
        public void RenamingParentDoesNotCancelConfirmedChildDeletion()
        {
            Guid folderId = Guid.NewGuid();
            Dictionary<string, SyncStateEntry> files = FileStates();
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
                [Change(1, SyncChangeKind.FileDeleted, files["ARCHIVE/FILE.TXT"].RemoteFileId!.Value),
                    Change(2, SyncChangeKind.FolderRenamed, folderId)]);

            Assert.That(RemoteDeleteConfirmation.CreatePathKeys(changes, files, DirectoryStates(folderId)),
                Is.EquivalentTo(new[] { "ARCHIVE/FILE.TXT" }));
        }

        [Test]
        public void SameFolderNameWithDifferentIdentityDoesNotConfirmDeletion()
        {
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
                [Change(1, SyncChangeKind.FolderDeleted, Guid.NewGuid())]);

            Assert.That(RemoteDeleteConfirmation.CreatePathKeys(changes, FileStates(), DirectoryStates(Guid.NewGuid())), Is.Empty);
        }

        [Test]
        public void LaterFolderDeleteConfirmsPreviouslyRestoredChildButNotSibling()
        {
            Guid folderId = Guid.NewGuid();
            Dictionary<string, SyncStateEntry> files = FileStates();
            files.Add("ARCHIVE-OTHER/FILE.TXT", new SyncStateEntry
            {
                RelativePath = "Archive-other/file.txt", Kind = SyncEntryKind.File, RemoteFileId = Guid.NewGuid(),
            });
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
                [Change(1, SyncChangeKind.FileRestored, files["ARCHIVE/FILE.TXT"].RemoteFileId!.Value),
                    Change(2, SyncChangeKind.FolderDeleted, folderId)]);

            Assert.That(RemoteDeleteConfirmation.CreatePathKeys(changes, files, DirectoryStates(folderId)),
                Is.EquivalentTo(new[] { "ARCHIVE", "ARCHIVE/FILE.TXT" }));
        }

        private static Dictionary<string, SyncStateEntry> DirectoryStates(Guid folderId)
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["ARCHIVE"] = new SyncStateEntry
                {
                    RelativePath = "Archive", Kind = SyncEntryKind.Directory, RemoteNodeId = folderId,
                },
            };
        }

        private static Dictionary<string, SyncStateEntry> FileStates()
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["ARCHIVE/FILE.TXT"] = new SyncStateEntry
                {
                    RelativePath = "Archive/file.txt", Kind = SyncEntryKind.File, RemoteFileId = Guid.NewGuid(),
                },
            };
        }

        private static SyncChangeDto Change(long cursor, SyncChangeKind kind, Guid itemId)
        {
            return new SyncChangeDto
            {
                Id = cursor, Kind = kind, ItemId = itemId, Name = "Archive",
                LayoutId = Guid.NewGuid(), ParentNodeId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            };
        }
    }
}
