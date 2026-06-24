// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Reflection;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.RemoteChanges;
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
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            object asyncResource = GetPrivateFieldValue(host, "_asyncResource");

            Assert.That(asyncResource, Is.TypeOf<CottonCloudClient>());
        }

        [Test]
        public async Task Create_WiresContinuousSyncCoordinators()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            Assert.That(host.App, Is.TypeOf<SyncApplicationService>());
            object localChanges = GetPrivateFieldValue(host.App, "_localChanges");
            object remoteChanges = GetPrivateFieldValue(host.App, "_remoteChanges");
            object periodicSync = GetPrivateFieldValue(host.App, "_periodicSync");

            Assert.Multiple(() =>
            {
                Assert.That(localChanges, Is.TypeOf<LocalChangeSyncCoordinator>());
                Assert.That(remoteChanges, Is.TypeOf<RealtimeRemoteChangeSyncCoordinator>());
                Assert.That(periodicSync, Is.TypeOf<PeriodicSyncCoordinator>());
            });
        }

        [Test]
        public async Task Create_WiresCloudFilesPlaceholderWriterIntoSyncEngine()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            object supervisor = GetPrivateFieldValue(host.App, "_supervisor");
            object runnerFactory = GetPrivateFieldValue(supervisor, "_runnerFactory");
            object repairWork = GetPrivateFieldValue(runnerFactory, "_work");
            object finalizationWork = GetPrivateFieldValue(repairWork, "_inner");
            object dehydrationWork = GetPrivateFieldValue(finalizationWork, "_inner");
            object remoteChangeAwareWork = GetPrivateFieldValue(dehydrationWork, "_inner");
            object syncEnginePairWork = GetPrivateFieldValue(remoteChangeAwareWork, "_inner");
            object syncEngine = GetPrivateFieldValue(syncEnginePairWork, "_syncEngine");
            object placeholderWriter = GetPrivateFieldValue(syncEngine, "_remoteFilePlaceholderWriter");

            Assert.That(placeholderWriter, Is.TypeOf<DesktopCloudFilesPlaceholderWriter>());
        }

        [Test]
        public async Task Create_WiresWindowsVirtualFilesFinalizationAndDehydrationBeforeRemoteChangeAcknowledgement()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            object supervisor = GetPrivateFieldValue(host.App, "_supervisor");
            object runnerFactory = GetPrivateFieldValue(supervisor, "_runnerFactory");
            object pairWork = GetPrivateFieldValue(runnerFactory, "_work");
            object finalizationWork = GetPrivateFieldValue(pairWork, "_inner");
            object dehydrationWork = GetPrivateFieldValue(finalizationWork, "_inner");
            object remoteChangeAwareWork = GetPrivateFieldValue(dehydrationWork, "_inner");

            Assert.Multiple(() =>
            {
                Assert.That(pairWork, Is.TypeOf<WindowsVirtualFilesDirectoryPlaceholderRepairPairWork>());
                Assert.That(finalizationWork, Is.TypeOf<WindowsVirtualFilesUploadFinalizationPairWork>());
                Assert.That(dehydrationWork, Is.TypeOf<WindowsVirtualFilesDehydrationPairWork>());
                Assert.That(remoteChangeAwareWork.GetType().Name, Is.EqualTo("RemoteChangeAwareSyncPairWork"));
            });
        }

        [Test]
        public async Task Create_WiresCloudFilesConnectionCoordinatorIntoSyncCoreLifecycle()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            object lifecycleComponents = GetPrivateFieldValue(host.App, "_syncCoreLifecycleComponents");

            Assert.That(
                lifecycleComponents,
                Has.One.TypeOf<WindowsCloudFilesSyncRootConnectionCoordinator>());
        }

        [Test]
        public async Task Create_WiresCloudFilesDeletionHandlerIntoSyncApplication()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new DesktopSyncApplicationFactory(paths);

            await using DesktopSyncApplicationHost host = factory.Create(new Uri("https://cotton.example.test/"));

            object deletionHandler = GetPrivateFieldValue(host.App, "_syncPairDeletionHandler");

            Assert.That(deletionHandler, Is.TypeOf<WindowsCloudFilesSyncPairDeletionHandler>());
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
            MethodInfo? createHandler = typeof(DesktopHttpClientFactory).GetMethod(
                "CreateHandler",
                BindingFlags.Static | BindingFlags.NonPublic);

            using var handler = (SocketsHttpHandler)(createHandler?.Invoke(null, null)
                ?? throw new InvalidOperationException("CreateHandler was not found."));

            Assert.That(handler.SslOptions.RemoteCertificateValidationCallback, Is.Null);
        }

        [Test]
        public void DesktopHttpClientFactory_ObservesAlreadyFaultedConnectCleanup()
        {
            var connectTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            Assert.That(IsTaskExceptionObserved(connectTask.Task), Is.False);

            DesktopHttpClientFactory.ObserveConnectCleanupFailure(connectTask.Task);

            Assert.That(IsTaskExceptionObserved(connectTask.Task), Is.True);
        }

        [Test]
        public async Task DesktopHttpClientFactory_ObservesLaterFaultedConnectCleanup()
        {
            var connectTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            DesktopHttpClientFactory.ObserveConnectCleanupFailure(connectTask.Task);
            connectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            await WaitForTaskExceptionObservationAsync(connectTask.Task);
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
            Assert.That(IsTaskExceptionObserved(connectTask.Task), Is.False);

            attempt.Dispose();
            await WaitForTaskExceptionObservationAsync(connectTask.Task);
        }

        [Test]
        public async Task DesktopHttpClientFactory_DisposeRemainingAttemptsSnapshotsAndClearsList()
        {
            TaskCompletionSource firstConnectTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondConnectTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<DesktopHttpClientFactory.ConnectAttempt> attempts =
            [
                new DesktopHttpClientFactory.ConnectAttempt(
                    IPAddress.Loopback,
                    new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                    firstConnectTask.Task),
                new DesktopHttpClientFactory.ConnectAttempt(
                    IPAddress.IPv6Loopback,
                    new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp),
                    secondConnectTask.Task),
            ];

            DesktopHttpClientFactory.DisposeRemainingAttempts(attempts);

            Assert.That(attempts, Is.Empty);

            firstConnectTask.SetException(new SocketException((int)SocketError.OperationAborted));
            secondConnectTask.SetException(new SocketException((int)SocketError.OperationAborted));

            await WaitForTaskExceptionObservationAsync(firstConnectTask.Task);
            await WaitForTaskExceptionObservationAsync(secondConnectTask.Task);
        }

        private static object GetPrivateFieldValue(object instance, string fieldName)
        {
            FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            return field!.GetValue(instance) ?? throw new InvalidOperationException(fieldName);
        }

        private static async Task WaitForTaskExceptionObservationAsync(Task task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!IsTaskExceptionObserved(task))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        private static bool IsTaskExceptionObserved(Task task)
        {
            FieldInfo? contingentPropertiesField = typeof(Task).GetField(
                "m_contingentProperties",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object? contingentProperties = contingentPropertiesField?.GetValue(task);
            if (contingentProperties is null)
            {
                return true;
            }

            FieldInfo? exceptionHolderField = contingentProperties.GetType().GetField(
                "m_exceptionsHolder",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object? exceptionHolder = exceptionHolderField?.GetValue(contingentProperties);
            if (exceptionHolder is null)
            {
                return true;
            }

            FieldInfo? isHandledField = exceptionHolder.GetType().GetField(
                "m_isHandled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return isHandledField is null || (bool)isHandledField.GetValue(exceptionHolder)!;
        }
    }
}
