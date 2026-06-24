// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public class DesktopCloudFilesRegistrationDiagnosticsSnapshotTests
    {
        [Test]
        public void Create_ReportsRegisteredAndMissingVirtualFilePairs()
        {
            Guid registeredPairId = Guid.NewGuid();
            Guid missingPairId = Guid.NewGuid();
            var registrar = new FakeStorageProviderSyncRootRegistrar([registeredPairId]);
            SyncPairSettings[] syncPairs =
            [
                CreateSyncPair(registeredPairId, "Registered", SyncPairMode.WindowsVirtualFiles),
                CreateSyncPair(missingPairId, "Missing", SyncPairMode.WindowsVirtualFiles),
                CreateSyncPair(Guid.NewGuid(), "Full mirror", SyncPairMode.FullMirror),
            ];

            DesktopCloudFilesRegistrationDiagnosticsSnapshot result =
                DesktopCloudFilesRegistrationDiagnosticsSnapshot.Create(syncPairs, registrar);

            Assert.Multiple(() =>
            {
                Assert.That(result.VirtualFilesSyncPairCount, Is.EqualTo(2));
                Assert.That(result.RegisteredSyncPairCount, Is.EqualTo(1));
                Assert.That(result.MissingSyncPairCount, Is.EqualTo(1));
                Assert.That(result.UnknownSyncPairCount, Is.EqualTo(0));
                Assert.That(result.SyncPairs.Select(static pair => pair.Status), Is.EqualTo(new[] { "registered", "not-registered" }));
                Assert.That(result.SyncPairs[0].IsRegistered, Is.True);
                Assert.That(result.SyncPairs[1].IsRegistered, Is.False);
            });
        }

        private static SyncPairSettings CreateSyncPair(
            Guid id,
            string displayName,
            SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = id,
                DisplayName = displayName,
                LocalRootPath = @"S:\CottonSyncVfsQa\" + displayName,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/" + displayName,
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private sealed class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            private readonly HashSet<Guid> _registeredSyncPairIds;

            public FakeStorageProviderSyncRootRegistrar(IEnumerable<Guid> registeredSyncPairIds)
            {
                _registeredSyncPairIds = registeredSyncPairIds.ToHashSet();
            }

            public bool IsSupported()
            {
                return true;
            }

            public bool IsRegistered(Guid syncPairId)
            {
                return _registeredSyncPairIds.Contains(syncPairId);
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void Unregister(Guid syncPairId, string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void UnregisterAllForCurrentUser()
            {
                throw new NotSupportedException();
            }
        }
    }
}
