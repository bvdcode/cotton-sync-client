// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Composition
{
    public class DesktopSyncApplicationFactoryTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-desktop-composition-" + Guid.NewGuid().ToString("N"));
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
        public async Task Create_TransfersCottonClientOwnershipToHost()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(host.Composition?.AsyncResourceType, Is.EqualTo(typeof(CottonCloudClient)));
        }

        [Test]
        public async Task Create_UsesInjectedHttpClientFactoryOnce()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            int calls = 0;
            Func<HttpClient> httpClientFactory = () =>
            {
                calls++;
                return new HttpClient();
            };
            DesktopSyncApplicationFactory factory = new(paths, httpClientFactory: httpClientFactory);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public async Task Create_WiresContinuousSyncCoordinators()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(host.App, Is.TypeOf<SyncApplicationService>());
            DesktopCompositionSnapshot composition = host.Composition
                ?? throw new InvalidOperationException("Composition snapshot is missing.");

            Assert.Multiple(() =>
            {
                Assert.That(composition.LocalChangeCoordinatorType, Is.EqualTo(typeof(LocalChangeSyncCoordinator)));
                Assert.That(composition.RemoteChangeCoordinatorType, Is.EqualTo(typeof(RealtimeRemoteChangeSyncCoordinator)));
                Assert.That(composition.PeriodicSyncCoordinatorType, Is.EqualTo(typeof(PeriodicSyncCoordinator)));
            });
        }

        [Test]
        public async Task Create_WiresCloudFilesPlaceholderWriterIntoSyncEngine()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(
                host.Composition?.PlaceholderWriterType,
                Is.EqualTo(typeof(DesktopCloudFilesPlaceholderWriter)));
        }

        [Test]
        public async Task Create_WiresRemoteChangeScopingAroundWindowsVirtualFilesWork()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            DesktopCompositionSnapshot composition = host.Composition
                ?? throw new InvalidOperationException("Composition snapshot is missing.");

            Assert.Multiple(() =>
            {
                Assert.That(composition.PairWorkType, Is.EqualTo(typeof(WindowsVirtualFilesDehydrationPairWork)));
                Assert.That(composition.RemoteChangePairWorkType, Is.EqualTo(typeof(RemoteChangeAwareSyncPairWork)));
                Assert.That(composition.FilePlaceholderRepairType, Is.EqualTo(typeof(WindowsVirtualFilesFilePlaceholderRepairPairWork)));
                Assert.That(composition.DirectoryPlaceholderRepairType, Is.EqualTo(typeof(WindowsVirtualFilesDirectoryPlaceholderRepairPairWork)));
                Assert.That(composition.UploadFinalizationType, Is.EqualTo(typeof(WindowsVirtualFilesUploadFinalizationPairWork)));
                Assert.That(composition.SyncEnginePairWorkType, Is.EqualTo(typeof(SyncEnginePairWork)));
            });
        }

        [Test]
        public async Task Create_WiresCloudFilesConnectionCoordinatorIntoSyncCoreLifecycle()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(
                host.Composition?.SyncCoreLifecycleType,
                Is.EqualTo(typeof(WindowsCloudFilesSyncRootConnectionCoordinator)));
        }

        [Test]
        public async Task Create_WiresCloudFilesDeletionHandlerIntoSyncApplication()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            DesktopSyncApplicationFactory factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(
                host.Composition?.SyncPairDeletionHandlerType,
                Is.EqualTo(typeof(WindowsCloudFilesSyncPairDeletionHandler)));
        }

        [Test]
        public void DesktopHttpClientFactory_KeepsDnsOrderForDualStackFallback()
        {
            IPAddress[] addresses =
            [
                IPAddress.Parse("2600:8801:fb00:36:6e1f:f7ff:fe3f:b0db"),
                IPAddress.Parse("10.0.0.10"),
            ];

            IReadOnlyList<IPAddress> ordered = DesktopHttpClientFactory.OrderAddressesForConnect(addresses);

            Assert.Multiple(() =>
            {
                Assert.That(ordered[0].AddressFamily, Is.EqualTo(AddressFamily.InterNetworkV6));
                Assert.That(ordered[0], Is.EqualTo(IPAddress.Parse("2600:8801:fb00:36:6e1f:f7ff:fe3f:b0db")));
                Assert.That(ordered[1].AddressFamily, Is.EqualTo(AddressFamily.InterNetwork));
            });
        }

        [Test]
        public void DesktopHttpClientFactory_DoesNotBypassCertificateValidation()
        {
            Assert.That(DesktopHttpClientFactory.HasCustomCertificateValidation(), Is.False);
        }

        [Test]
        public async Task DesktopHttpClientFactory_ObservesAlreadyFaultedConnectCleanup()
        {
            TaskCompletionSource connectTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            Task cleanupTask = DesktopHttpClientFactory.ObserveConnectCleanupFailureAsync(connectTask.Task);
            await cleanupTask;

            Assert.That(cleanupTask.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task DesktopHttpClientFactory_ObservesLaterFaultedConnectCleanup()
        {
            TaskCompletionSource connectTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task cleanupTask = DesktopHttpClientFactory.ObserveConnectCleanupFailureAsync(connectTask.Task);
            connectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(cleanupTask.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task DesktopHttpClientFactory_FallbackDelayKeepsPendingConnectAttemptOwnedUntilCleanup()
        {
            TaskCompletionSource connectTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            DesktopHttpClientFactory.ConnectAttempt attempt =
                new(IPAddress.Loopback, socket, connectTask.Task);
            List<DesktopHttpClientFactory.ConnectAttempt> attempts = [attempt];

            DesktopHttpClientFactory.ConnectAttempt? completedAttempt =
                await DesktopHttpClientFactory.WaitForCompletedConnectOrFallbackDelayAsync(
                        attempts,
                        TimeSpan.Zero,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(completedAttempt, Is.Null);
                Assert.That(attempts, Is.EqualTo(new[] { attempt }));
            });

            connectTask.SetException(new SocketException((int)SocketError.OperationAborted));
            attempt.Dispose();
            await attempt.CleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(attempt.CleanupTask.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task DesktopHttpClientFactory_DisposeRemainingAttemptsSnapshotsAndClearsList()
        {
            TaskCompletionSource firstConnectTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondConnectTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DesktopHttpClientFactory.ConnectAttempt firstAttempt = new(
                IPAddress.Loopback,
                new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                firstConnectTask.Task);
            DesktopHttpClientFactory.ConnectAttempt secondAttempt = new(
                IPAddress.IPv6Loopback,
                new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp),
                secondConnectTask.Task);
            List<DesktopHttpClientFactory.ConnectAttempt> attempts =
            [
                firstAttempt,
                secondAttempt,
            ];

            DesktopHttpClientFactory.DisposeRemainingAttempts(attempts);

            Assert.That(attempts, Is.Empty);

            firstConnectTask.SetException(new SocketException((int)SocketError.OperationAborted));
            secondConnectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            await Task.WhenAll(firstAttempt.CleanupTask, secondAttempt.CleanupTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(firstAttempt.CleanupTask.IsCompletedSuccessfully, Is.True);
                Assert.That(secondAttempt.CleanupTask.IsCompletedSuccessfully, Is.True);
            });
        }
    }
}
