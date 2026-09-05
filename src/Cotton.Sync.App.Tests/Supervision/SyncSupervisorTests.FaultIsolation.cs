// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using System.Text.Json;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Supervision
{
    public partial class SyncSupervisorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task SyncAllAsync_DiskFullDuringDownloadPreservesFailedPairAndContinuesHealthyPair(bool resume)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-supervisor-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            SyncSupervisor? supervisor = null;
            try
            {
                SyncPairSettings failedPair = CreatePair("Documents", isEnabled: true);
                SyncPairSettings healthyPair = CreatePair("Pictures", isEnabled: true);
                failedPair.LocalRootPath = Path.Combine(root, "documents");
                healthyPair.LocalRootPath = Path.Combine(root, "pictures");
                Directory.CreateDirectory(failedPair.LocalRootPath);
                Directory.CreateDirectory(healthyPair.LocalRootPath);
                string databasePath = Path.Combine(root, "state.db");
                SqliteSyncStateStore stateStore = new(databasePath);
                FaultIsolationRemote remote = new();
                remote.SetContent(failedPair.RemoteRootNodeId, "original documents");
                remote.SetContent(healthyPair.RemoteRootNodeId, "original pictures");
                SyncEngine engine = new(new LocalFileScanner(), remote, remote, stateStore);
                InMemoryAppStatusPublisher publisher = new();
                supervisor = new SyncSupervisor(
                    new FakeSyncPairSettingsStore([failedPair, healthyPair]),
                    new SyncPairRunnerFactory(new SyncEnginePairWork(engine)),
                    publisher);
                await supervisor.StartAsync();
                await supervisor.SyncAllAsync();

                SyncStateEntry failedBaseline = (await stateStore.LoadPairAsync(failedPair.Id.ToString("D"))).Single();
                DateTime? failedLastSuccess = publisher.Current.SyncPairs.Single(status => status.SyncPairId == failedPair.Id)
                    .LastSuccessfulSyncAtUtc;
                remote.SetContent(failedPair.RemoteRootNodeId, "updated documents");
                remote.SetContent(healthyPair.RemoteRootNodeId, "updated pictures");
                remote.DiskFullRootNodeId = failedPair.RemoteRootNodeId;
                if (resume)
                {
                    await supervisor.PauseAllAsync();
                }

                IOException? failure = Assert.ThrowsAsync<IOException>(async () =>
                {
                    if (resume)
                    {
                        await supervisor.ResumeAllAsync();
                    }
                    else
                    {
                        await supervisor.SyncAllAsync();
                    }
                });

                SqliteSyncStateStore reopenedStore = new(databasePath);
                SyncStateEntry retainedBaseline = (await reopenedStore.LoadPairAsync(failedPair.Id.ToString("D"))).Single();
                SyncPairStatus failedStatus = publisher.Current.SyncPairs.Single(status => status.SyncPairId == failedPair.Id);
                SyncPairStatus healthyStatus = publisher.Current.SyncPairs.Single(status => status.SyncPairId == healthyPair.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(failure!.HResult, Is.EqualTo(FaultIsolationRemote.DiskFullHResult));
                    Assert.That(remote.PartialWriteCount, Is.EqualTo(1));
                    Assert.That(failedStatus.State, Is.EqualTo(SyncPairRunState.Error));
                    Assert.That(failedStatus.LastError, Does.Contain("Local disk is full").And.Contain("Free space"));
                    Assert.That(failedStatus.LastSuccessfulSyncAtUtc, Is.EqualTo(failedLastSuccess));
                    Assert.That(healthyStatus.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(healthyStatus.LastError, Is.Null);
                    Assert.That(JsonSerializer.Serialize(retainedBaseline), Is.EqualTo(JsonSerializer.Serialize(failedBaseline)));
                    Assert.That(
                        File.ReadAllBytes(Path.Combine(failedPair.LocalRootPath, FaultIsolationRemote.RelativePath)),
                        Is.EqualTo(Encoding.UTF8.GetBytes("original documents")));
                    Assert.That(Directory.GetFiles(failedPair.LocalRootPath, "*.download", SearchOption.AllDirectories), Is.Empty);
                });
                await AssertPairContentAndBaselineAsync(healthyPair, reopenedStore, remote, "updated pictures");

                remote.SetContent(healthyPair.RemoteRootNodeId, "pictures changed again");
                await supervisor.SyncNowAsync(healthyPair.Id);
                await AssertPairContentAndBaselineAsync(healthyPair, reopenedStore, remote, "pictures changed again");
                Assert.That(publisher.Current.SyncPairs.Single(status => status.SyncPairId == failedPair.Id).State,
                    Is.EqualTo(SyncPairRunState.Error));

                remote.DiskFullRootNodeId = null;
                await supervisor.SyncNowAsync(failedPair.Id);
                await AssertPairContentAndBaselineAsync(failedPair, reopenedStore, remote, "updated documents");
                Assert.Multiple(() =>
                {
                    Assert.That(publisher.Current.SyncPairs.All(status => status.State == SyncPairRunState.Idle), Is.True);
                    Assert.That(publisher.Current.SyncPairs.All(status => status.LastError is null), Is.True);
                    Assert.That(remote.PartialWriteCount, Is.EqualTo(1));
                });
            }
            finally
            {
                if (supervisor is not null)
                {
                    await supervisor.StopAsync();
                }

                Directory.Delete(root, recursive: true);
            }
        }

        private static async Task AssertPairContentAndBaselineAsync(
            SyncPairSettings pair,
            SqliteSyncStateStore stateStore,
            FaultIsolationRemote remote,
            string expectedContent)
        {
            SyncStateEntry baseline = (await stateStore.LoadPairAsync(pair.Id.ToString("D"))).Single();
            byte[] content = await File.ReadAllBytesAsync(Path.Combine(pair.LocalRootPath, FaultIsolationRemote.RelativePath));
            Assert.Multiple(() =>
            {
                Assert.That(content, Is.EqualTo(Encoding.UTF8.GetBytes(expectedContent)));
                Assert.That(baseline.SyncPairId, Is.EqualTo(pair.Id.ToString("D")));
                Assert.That(baseline.RelativePath, Is.EqualTo(FaultIsolationRemote.RelativePath));
                Assert.That(baseline.RemoteFileId, Is.EqualTo(remote.GetFile(pair.RemoteRootNodeId).Id));
                Assert.That(baseline.LocalContentHash, Is.EqualTo(remote.GetFile(pair.RemoteRootNodeId).ContentHash));
                Assert.That(baseline.RemoteContentHash, Is.EqualTo(remote.GetFile(pair.RemoteRootNodeId).ContentHash));
                Assert.That(baseline.LocalSizeBytes, Is.EqualTo(content.LongLength));
                Assert.That(baseline.RemoteSizeBytes, Is.EqualTo(content.LongLength));
            });
        }
    }
}
