// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Tests.TestSupport;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Cotton.Sync.App.Tests.Supervision
{
    public partial class SyncSupervisorTests
    {
        [Test]
        public async Task StartAsync_CreatesStartsRunnersAndPublishesStatus()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: false);
            FakeSyncPairSettingsStore store = new FakeSyncPairSettingsStore([documents, pictures]);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher(new SyncAppStatus(true, [], DateTime.UtcNow));
            SyncSupervisor supervisor = new SyncSupervisor(store, factory, publisher);

            await supervisor.StartAsync();

            Assert.Multiple(() =>
            {
                Assert.That(store.InitializeCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners, Has.Count.EqualTo(2));
                Assert.That(factory.CreatedRunners[documents.Id].StartCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[pictures.Id].StartCallCount, Is.EqualTo(1));
                Assert.That(publisher.Current.IsAuthenticated, Is.True);
                Assert.That(
                    publisher.Current.SyncPairs.Select(status => status.State),
                    Is.EqualTo(new[] { SyncPairRunState.Idle, SyncPairRunState.Disabled }));
            });
        }

        [Test]
        public async Task PauseAndResumeAsync_UpdateSelectedRunnerAndPublishStatus()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents, pictures]),
                factory,
                publisher);
            await supervisor.StartAsync();

            await supervisor.PauseAsync(pictures.Id);
            SyncPairRunState pausedState = factory.CreatedRunners[pictures.Id].Status.State;
            await supervisor.ResumeAsync(pictures.Id);

            Assert.Multiple(() =>
            {
                Assert.That(pausedState, Is.EqualTo(SyncPairRunState.Paused));
                Assert.That(factory.CreatedRunners[documents.Id].Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(factory.CreatedRunners[pictures.Id].Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(factory.CreatedRunners[documents.Id].SyncNowCallCount, Is.Zero);
                Assert.That(factory.CreatedRunners[pictures.Id].SyncNowCallCount, Is.EqualTo(1));
                Assert.That(publisher.Current.SyncPairs.Select(status => status.State), Is.All.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task ResumeAllAsync_RequestsSyncForEnabledRunnersOnly()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: false);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents, pictures]),
                factory,
                new InMemoryAppStatusPublisher());
            await supervisor.StartAsync();
            await supervisor.PauseAllAsync();

            await supervisor.ResumeAllAsync();

            Assert.Multiple(() =>
            {
                Assert.That(factory.CreatedRunners[documents.Id].ResumeCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[pictures.Id].ResumeCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[documents.Id].SyncNowCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[pictures.Id].SyncNowCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task ResumeAsync_DoesNotBlockStopWhileResumeSyncIsRunning()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                new InMemoryAppStatusPublisher());
            await supervisor.StartAsync();
            await supervisor.PauseAsync(documents.Id);
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;

            Task resume = supervisor.ResumeAsync(documents.Id);
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            Task stop = supervisor.StopAsync();
            bool stopReachedRunner = await runner.WaitForStopAsync(TimeSpan.FromMilliseconds(250));

            runner.ReleaseSyncNow();
            await Task.WhenAll(resume, stop).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(stopReachedRunner, Is.True);
                Assert.That(runner.ResumeCallCount, Is.EqualTo(1));
                Assert.That(runner.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(runner.StopCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SyncNowAsync_DelegatesToSelectedRunner()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents, pictures]),
                factory,
                new InMemoryAppStatusPublisher());
            await supervisor.StartAsync();

            await supervisor.SyncNowAsync(pictures.Id);

            Assert.Multiple(() =>
            {
                Assert.That(factory.CreatedRunners[documents.Id].SyncNowCallCount, Is.Zero);
                Assert.That(factory.CreatedRunners[pictures.Id].SyncNowCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SyncNowAsync_PublishesSyncingStatusWhileRunnerIsActive()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                publisher);
            await supervisor.StartAsync();
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;

            Task sync = supervisor.SyncNowAsync(documents.Id);
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            SyncPairRunState publishedWhileRunning = publisher.Current.SyncPairs.Single().State;

            runner.ReleaseSyncNow();
            await sync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(publishedWhileRunning, Is.EqualTo(SyncPairRunState.Syncing));
                Assert.That(publisher.Current.SyncPairs.Single().State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_RepublishesActiveStatusWhileRunnerRemainsActive()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            RecordingObserver<SyncAppStatus> statusObserver = new RecordingObserver<SyncAppStatus>();
            using IDisposable subscription = publisher.Subscribe(statusObserver);
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                publisher,
                activeStatusPublishInterval: TimeSpan.FromMilliseconds(20));
            await supervisor.StartAsync();
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;

            Task sync = supervisor.SyncNowAsync(documents.Id);
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            await WaitForStatusCountAsync(
                statusObserver,
                status => status.SyncPairs.SingleOrDefault()?.State == SyncPairRunState.Syncing,
                minimumCount: 2,
                timeout: TimeSpan.FromSeconds(2));

            runner.ReleaseSyncNow();
            await sync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(
                    statusObserver.Values.Count(status => status.SyncPairs.SingleOrDefault()?.State == SyncPairRunState.Syncing),
                    Is.GreaterThanOrEqualTo(2));
                Assert.That(publisher.Current.SyncPairs.Single().State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_PublishesErrorWhenActiveRunnerFailsAsynchronously()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                publisher);
            await supervisor.StartAsync();
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;
            runner.SyncNowException = new InvalidOperationException("Documents failed.");

            Task sync = supervisor.SyncNowAsync(documents.Id);
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            Assert.That(publisher.Current.SyncPairs.Single().State, Is.EqualTo(SyncPairRunState.Syncing));

            runner.ReleaseSyncNow();
            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await sync.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Is.EqualTo("Documents failed."));
                Assert.That(publisher.Current.SyncPairs.Single().State, Is.EqualTo(SyncPairRunState.Error));
            });
        }

        [Test]
        public async Task SyncAllAsync_PublishesSyncingStatusWhileRunnerIsActive()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                publisher);
            await supervisor.StartAsync();
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;

            Task sync = supervisor.SyncAllAsync();
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            SyncPairRunState publishedWhileRunning = publisher.Current.SyncPairs.Single().State;

            runner.ReleaseSyncNow();
            await sync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(publishedWhileRunning, Is.EqualTo(SyncPairRunState.Syncing));
                Assert.That(publisher.Current.SyncPairs.Single().State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncAllAsync_ContinuesOtherRunnersAndPublishesStatusWhenRunnerFails()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents, pictures]),
                factory,
                publisher);
            await supervisor.StartAsync();
            factory.CreatedRunners[documents.Id].SyncNowException = new InvalidOperationException("Documents failed.");

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await supervisor.SyncAllAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Is.EqualTo("Documents failed."));
                Assert.That(factory.CreatedRunners[documents.Id].SyncNowCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[pictures.Id].SyncNowCallCount, Is.EqualTo(1));
                Assert.That(
                    publisher.Current.SyncPairs.Select(status => status.State),
                    Is.EqualTo(new[] { SyncPairRunState.Error, SyncPairRunState.Idle }));
            });
        }

    }
}
