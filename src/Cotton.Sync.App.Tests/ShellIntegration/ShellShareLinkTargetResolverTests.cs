// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Runtime.CompilerServices;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.ShellIntegration
{
    public class ShellShareLinkTargetResolverTests
    {
        [Test]
        public async Task ResolveAsync_ResolvesSyncedFileFromLocalPathWithoutFullStateLoad()
        {
            SyncPairSettings syncPair = CreatePair(@"C:\Cloud");
            Guid remoteFileId = Guid.NewGuid();
            var stateStore = new FakeSyncStateStore([
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Docs/report.pdf",
                    Kind = SyncEntryKind.File,
                    RemoteFileId = remoteFileId,
                },
            ]);
            var resolver = new ShellShareLinkTargetResolver(
                new FakeSyncPairSettingsStore([syncPair]),
                stateStore);

            ShellShareLinkTarget target = await resolver.ResolveAsync(@"C:\Cloud\Docs\report.pdf");

            Assert.Multiple(() =>
            {
                Assert.That(target.Status, Is.EqualTo(ShellShareLinkTargetStatus.Resolved));
                Assert.That(target.Kind, Is.EqualTo(ShellShareLinkTargetKind.File));
                Assert.That(target.SyncPairId, Is.EqualTo(syncPair.Id));
                Assert.That(target.RelativePath, Is.EqualTo("Docs/report.pdf"));
                Assert.That(target.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(target.CanCreateShareLink, Is.True);
                Assert.That(stateStore.GetCalls, Is.EqualTo(1));
                Assert.That(stateStore.LoadPairCalls, Is.Zero);
                Assert.That(stateStore.LoadPairEntriesCalls, Is.Zero);
            });
        }

        [Test]
        public async Task ResolveAsync_ResolvesSyncedDirectoryAndRoot()
        {
            Guid rootNodeId = Guid.NewGuid();
            Guid childNodeId = Guid.NewGuid();
            SyncPairSettings syncPair = CreatePair(@"C:\Cloud", remoteRootNodeId: rootNodeId);
            var stateStore = new FakeSyncStateStore([
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Projects",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = childNodeId,
                },
            ]);
            var resolver = new ShellShareLinkTargetResolver(
                new FakeSyncPairSettingsStore([syncPair]),
                stateStore);

            ShellShareLinkTarget root = await resolver.ResolveAsync(@"C:\Cloud");
            ShellShareLinkTarget directory = await resolver.ResolveAsync(@"C:\Cloud\Projects");

            Assert.Multiple(() =>
            {
                Assert.That(root.Status, Is.EqualTo(ShellShareLinkTargetStatus.Resolved));
                Assert.That(root.Kind, Is.EqualTo(ShellShareLinkTargetKind.Directory));
                Assert.That(root.RemoteNodeId, Is.EqualTo(rootNodeId));
                Assert.That(root.RelativePath, Is.Empty);
                Assert.That(directory.Status, Is.EqualTo(ShellShareLinkTargetStatus.Resolved));
                Assert.That(directory.Kind, Is.EqualTo(ShellShareLinkTargetKind.Directory));
                Assert.That(directory.RemoteNodeId, Is.EqualTo(childNodeId));
                Assert.That(directory.RelativePath, Is.EqualTo("Projects"));
                Assert.That(stateStore.GetCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task ResolveAsync_UsesMostSpecificSyncRoot()
        {
            SyncPairSettings parent = CreatePair(@"C:\Cloud");
            SyncPairSettings child = CreatePair(@"C:\Cloud\Shared");
            Guid childFileId = Guid.NewGuid();
            var stateStore = new FakeSyncStateStore([
                new SyncStateEntry
                {
                    SyncPairId = child.Id.ToString("D"),
                    RelativePath = "report.txt",
                    Kind = SyncEntryKind.File,
                    RemoteFileId = childFileId,
                },
            ]);
            var resolver = new ShellShareLinkTargetResolver(
                new FakeSyncPairSettingsStore([parent, child]),
                stateStore);

            ShellShareLinkTarget target = await resolver.ResolveAsync(@"C:\Cloud\Shared\report.txt");

            Assert.Multiple(() =>
            {
                Assert.That(target.Status, Is.EqualTo(ShellShareLinkTargetStatus.Resolved));
                Assert.That(target.SyncPairId, Is.EqualTo(child.Id));
                Assert.That(target.RelativePath, Is.EqualTo("report.txt"));
                Assert.That(target.RemoteFileId, Is.EqualTo(childFileId));
            });
        }

        [Test]
        public async Task ResolveAsync_ReturnsExplicitNonShareableStatuses()
        {
            SyncPairSettings disabled = CreatePair(@"C:\Disabled", isEnabled: false);
            SyncPairSettings enabled = CreatePair(@"C:\Cloud");
            var resolver = new ShellShareLinkTargetResolver(
                new FakeSyncPairSettingsStore([disabled, enabled]),
                new FakeSyncStateStore([]));

            ShellShareLinkTarget outside = await resolver.ResolveAsync(@"C:\Other\file.txt");
            ShellShareLinkTarget disabledTarget = await resolver.ResolveAsync(@"C:\Disabled\file.txt");
            ShellShareLinkTarget ignored = await resolver.ResolveAsync(@"C:\Cloud\.cotton-sync\state.db");
            ShellShareLinkTarget missing = await resolver.ResolveAsync(@"C:\Cloud\local-only.txt");

            Assert.Multiple(() =>
            {
                Assert.That(outside.Status, Is.EqualTo(ShellShareLinkTargetStatus.OutsideSyncRoot));
                Assert.That(disabledTarget.Status, Is.EqualTo(ShellShareLinkTargetStatus.SyncPairDisabled));
                Assert.That(ignored.Status, Is.EqualTo(ShellShareLinkTargetStatus.IgnoredPath));
                Assert.That(missing.Status, Is.EqualTo(ShellShareLinkTargetStatus.MissingBaseline));
                Assert.That(outside.CanCreateShareLink, Is.False);
                Assert.That(disabledTarget.CanCreateShareLink, Is.False);
                Assert.That(ignored.CanCreateShareLink, Is.False);
                Assert.That(missing.CanCreateShareLink, Is.False);
            });
        }

        [Test]
        public async Task ResolveAsync_ReturnsMissingIdentityForStateWithoutRemoteId()
        {
            SyncPairSettings syncPair = CreatePair(@"C:\Cloud");
            var resolver = new ShellShareLinkTargetResolver(
                new FakeSyncPairSettingsStore([syncPair]),
                new FakeSyncStateStore([
                    new SyncStateEntry
                    {
                        SyncPairId = syncPair.Id.ToString("D"),
                        RelativePath = "Docs/report.pdf",
                        Kind = SyncEntryKind.File,
                    },
                ]));

            ShellShareLinkTarget target = await resolver.ResolveAsync(@"C:\Cloud\Docs\report.pdf");

            Assert.Multiple(() =>
            {
                Assert.That(target.Status, Is.EqualTo(ShellShareLinkTargetStatus.MissingRemoteIdentity));
                Assert.That(target.Kind, Is.EqualTo(ShellShareLinkTargetKind.File));
                Assert.That(target.CanCreateShareLink, Is.False);
            });
        }

        private static SyncPairSettings CreatePair(
            string localRootPath,
            bool isEnabled = true,
            Guid? remoteRootNodeId = null)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Cloud",
                LocalRootPath = localRootPath,
                RemoteRootNodeId = remoteRootNodeId ?? Guid.NewGuid(),
                RemoteDisplayPath = "/",
                IsEnabled = isEnabled,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }

        private class FakeSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private readonly IReadOnlyList<SyncPairSettings> _syncPairs;

            public FakeSyncPairSettingsStore(IReadOnlyList<SyncPairSettings> syncPairs)
            {
                _syncPairs = syncPairs;
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs);
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs.FirstOrDefault(pair => pair.Id == syncPairId));
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<(string SyncPairId, string RelativePath), SyncStateEntry> _entries;

            public FakeSyncStateStore(IEnumerable<SyncStateEntry> entries)
            {
                _entries = entries.ToDictionary(
                    entry => (entry.SyncPairId, SyncPath.ToKey(entry.RelativePath)),
                    entry => entry);
            }

            public int GetCalls { get; private set; }

            public int LoadPairCalls { get; private set; }

            public int LoadPairEntriesCalls { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                LoadPairCalls++;
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>(
                    _entries.Values.Where(entry => entry.SyncPairId == syncPairId).ToList());
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCalls++;
                foreach (SyncStateEntry entry in _entries.Values.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(DateTime.UtcNow);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor
                {
                    SyncPairId = syncPairId,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                GetCalls++;
                _entries.TryGetValue((syncPairId, SyncPath.ToKey(relativePath)), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
