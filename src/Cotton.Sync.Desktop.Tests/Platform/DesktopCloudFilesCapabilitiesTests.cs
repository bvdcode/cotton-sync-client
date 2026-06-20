// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class DesktopCloudFilesCapabilitiesTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "cotton-cloud-files-capabilities-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void CreateSyncPairModeCapabilities_ReportsCurrentHostSupportWithoutThrowing()
        {
            var snapshot = DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.WindowsVirtualFilesDetails, Is.Not.Empty);
                if (OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
                {
                    Assert.That(snapshot.WindowsVirtualFilesDetails, Does.Contain("Cloud Files API"));
                    if (snapshot.WindowsVirtualFilesDetails.Contains("shell helper", StringComparison.Ordinal)
                        || snapshot.WindowsVirtualFilesDetails.Contains("StorageProvider", StringComparison.Ordinal))
                    {
                        Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.False);
                    }
                    else
                    {
                        Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.True);
                    }
                }
                else
                {
                    Assert.That(snapshot.IsWindowsVirtualFilesSupported, Is.False);
                    Assert.That(snapshot.WindowsVirtualFilesDetails, Does.Contain("Windows"));
                }
            });
        }

        [Test]
        public void CreateSelfTestCapability_SkipsWhenBasicCapabilityIsUnavailable()
        {
            var adapter = new FakeCloudFilesAdapter();

            DesktopCloudFilesSelfTestCapabilitySnapshot snapshot =
                DesktopCloudFilesCapabilities.CreateSelfTestCapability(
                    new SyncPairModeCapabilitySnapshot(
                        false,
                        "Windows virtual files require the Windows Cloud Files API."),
                    adapter,
                    CreateProbeRoot);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Passed, Is.False);
                Assert.That(snapshot.Skipped, Is.True);
                Assert.That(snapshot.Details, Does.Contain("Cloud Files API"));
                Assert.That(adapter.ConnectedSyncPairs, Is.Empty);
                Assert.That(adapter.UnregisteredSyncPairs, Is.Empty);
            });
        }

        [Test]
        public void CreateSelfTestCapability_VerifiesConnectBoundaryAndCleansUpProbeRoot()
        {
            var adapter = new FakeCloudFilesAdapter();
            string probeRoot = CreateProbeRoot();

            DesktopCloudFilesSelfTestCapabilitySnapshot snapshot =
                DesktopCloudFilesCapabilities.CreateSelfTestCapability(
                    new SyncPairModeCapabilitySnapshot(
                        true,
                        "Windows Cloud Files API is available."),
                    adapter,
                    () => probeRoot);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Passed, Is.True);
                Assert.That(snapshot.Skipped, Is.False);
                Assert.That(snapshot.Details, Does.Contain("CfConnectSyncRoot"));
                Assert.That(adapter.ConnectedSyncPairs, Has.Count.EqualTo(1));
                Assert.That(adapter.UnregisteredSyncPairs, Has.Count.EqualTo(1));
                Assert.That(adapter.DisconnectedKeys, Is.EqualTo(new[] { new WindowsCloudFilesConnectionKey(42) }));
                Assert.That(Directory.Exists(probeRoot), Is.False);
            });
        }

        [Test]
        public void CreateSelfTestCapability_ReusesStableSyncPairIdentityForCleanup()
        {
            var adapter = new FakeCloudFilesAdapter();
            string firstProbeRoot = CreateProbeRoot();
            string secondProbeRoot = CreateProbeRoot();

            DesktopCloudFilesCapabilities.CreateSelfTestCapability(
                new SyncPairModeCapabilitySnapshot(
                    true,
                    "Windows Cloud Files API is available."),
                adapter,
                () => firstProbeRoot);
            DesktopCloudFilesCapabilities.CreateSelfTestCapability(
                new SyncPairModeCapabilitySnapshot(
                    true,
                    "Windows Cloud Files API is available."),
                adapter,
                () => secondProbeRoot);

            Guid[] connectedIds = adapter.ConnectedSyncPairs.Select(static pair => pair.Id).ToArray();
            Guid[] unregisteredIds = adapter.UnregisteredSyncPairs.Select(static pair => pair.Id).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(connectedIds, Has.Length.EqualTo(2));
                Assert.That(connectedIds.Distinct().Count(), Is.EqualTo(1));
                Assert.That(unregisteredIds, Is.EqualTo(connectedIds));
                Assert.That(adapter.ConnectedSyncPairs.Select(static pair => pair.LocalRootPath), Is.EqualTo(new[] { firstProbeRoot, secondProbeRoot }));
                Assert.That(Directory.Exists(firstProbeRoot), Is.False);
                Assert.That(Directory.Exists(secondProbeRoot), Is.False);
            });
        }

        [Test]
        public void CreateSelfTestCapability_FailsWhenConnectBoundaryFailsAndStillCleansUp()
        {
            var adapter = new FakeCloudFilesAdapter
            {
                ConnectException = new WindowsCloudFilesNativeException(
                    "CfConnectSyncRoot",
                    unchecked((int)0x80070090)),
            };
            string probeRoot = CreateProbeRoot();

            DesktopCloudFilesSelfTestCapabilitySnapshot snapshot =
                DesktopCloudFilesCapabilities.CreateSelfTestCapability(
                    new SyncPairModeCapabilitySnapshot(
                        true,
                        "Windows Cloud Files API is available."),
                    adapter,
                    () => probeRoot);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Passed, Is.False);
                Assert.That(snapshot.Skipped, Is.False);
                Assert.That(snapshot.Details, Does.Contain("CfConnectSyncRoot"));
                Assert.That(snapshot.Details, Does.Contain("0x80070090"));
                Assert.That(adapter.ConnectedSyncPairs, Has.Count.EqualTo(1));
                Assert.That(adapter.UnregisteredSyncPairs, Has.Count.EqualTo(1));
                Assert.That(Directory.Exists(probeRoot), Is.False);
            });
        }

        private string CreateProbeRoot()
        {
            return Path.Combine(
                _tempDirectory,
                "probe-" + Guid.NewGuid().ToString("N"));
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<SyncPairSettings> ConnectedSyncPairs { get; } = [];

            public List<SyncPairSettings> UnregisteredSyncPairs { get; } = [];

            public List<WindowsCloudFilesConnectionKey> DisconnectedKeys { get; } = [];

            public Exception? ConnectException { get; init; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                UnregisteredSyncPairs.Add(syncPair);
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                ConnectedSyncPairs.Add(syncPair);
                if (ConnectException is not null)
                {
                    throw ConnectException;
                }

                return new WindowsCloudFilesConnection(
                    syncPair.LocalRootPath,
                    new WindowsCloudFilesConnectionKey(42),
                    DisconnectedKeys.Add);
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
