// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [Test]
        public async Task ExportDiagnosticsAsync_IncludesCurrentAndAggregateProgressWithoutPrivatePaths()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            Guid syncPairId = Guid.NewGuid();
            DateTime startedAtUtc = new(2026, 7, 17, 6, 30, 0, DateTimeKind.Utc);
            DateTime occurredAtUtc = startedAtUtc.AddSeconds(3);

            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            host.TransferProgressPublisher.Publish(new AppTransferProgress(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/private-track.flac",
                4096,
                8192,
                isCompleted: false,
                occurredAtUtc,
                speedBytesPerSecond: 1024,
                estimatedTimeRemaining: TimeSpan.FromSeconds(4)));
            host.TransferProgressPublisher.Publish(new AppTransferProgress(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/private-track-2.flac",
                2048,
                4096,
                isCompleted: false,
                occurredAtUtc.AddSeconds(1),
                speedBytesPerSecond: 512,
                estimatedTimeRemaining: TimeSpan.FromSeconds(4)));
            host.RunProgressPublisher.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.HydratingCloudFiles,
                filesCompleted: 4,
                filesTotal: 10,
                currentPath: "Music/private-track.flac",
                startedAtUtc,
                isCompleted: false,
                occurredAtUtc,
                bytesCompleted: 4096,
                bytesTotal: 8192,
                causes: SyncRunCause.Manual | SyncRunCause.LocalChange,
                isFull: false,
                requestedPathCount: 2));

            JsonElement currentTransfers = await ReadDiagnosticsRootAsync(controller, "currentTransfers");
            JsonElement aggregateRunProgress = await ReadDiagnosticsRootAsync(controller, "aggregateRunProgress");

            Assert.Multiple(() =>
            {
                Assert.That(currentTransfers.GetArrayLength(), Is.EqualTo(2));
                Assert.That(
                    currentTransfers.EnumerateArray().Select(item => item.GetProperty("direction").GetString()),
                    Is.All.EqualTo("download"));
                Assert.That(
                    currentTransfers.EnumerateArray().Select(item => item.GetProperty("relativePath").GetString()),
                    Is.All.EqualTo("[transfer-path]"));
                Assert.That(
                    currentTransfers.EnumerateArray()
                        .Select(item => item.GetProperty("transferredBytes").GetInt64())
                        .Order(),
                    Is.EqualTo(new long[] { 2048, 4096 }));
                Assert.That(
                    currentTransfers.EnumerateArray()
                        .Select(item => item.GetProperty("totalBytes").GetInt64())
                        .Order(),
                    Is.EqualTo(new long[] { 4096, 8192 }));
                Assert.That(aggregateRunProgress.GetArrayLength(), Is.EqualTo(1));
                Assert.That(
                    aggregateRunProgress[0].GetProperty("stage").GetString(),
                    Is.EqualTo("hydratingCloudFiles"));
                Assert.That(aggregateRunProgress[0].GetProperty("currentPath").GetString(), Is.EqualTo("[run-current-path]"));
                Assert.That(aggregateRunProgress[0].GetProperty("filesCompleted").GetInt32(), Is.EqualTo(4));
                Assert.That(aggregateRunProgress[0].GetProperty("filesTotal").GetInt32(), Is.EqualTo(10));
                Assert.That(aggregateRunProgress[0].GetProperty("bytesCompleted").GetInt64(), Is.EqualTo(4096));
                Assert.That(aggregateRunProgress[0].GetProperty("bytesTotal").GetInt64(), Is.EqualTo(8192));
                Assert.That(aggregateRunProgress[0].GetProperty("isFull").GetBoolean(), Is.False);
                Assert.That(aggregateRunProgress[0].GetProperty("requestedPathCount").GetInt32(), Is.EqualTo(2));
                Assert.That(
                    aggregateRunProgress[0].GetProperty("causes").GetString(),
                    Does.Contain("manual").And.Contain("localChange"));
            });

            host.TransferProgressPublisher.Publish(new AppTransferProgress(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/private-track-2.flac",
                4096,
                4096,
                isCompleted: true,
                occurredAtUtc.AddSeconds(2)));
            JsonElement remainingTransfers = await ReadDiagnosticsRootAsync(controller, "currentTransfers");
            Assert.Multiple(() =>
            {
                Assert.That(remainingTransfers.GetArrayLength(), Is.EqualTo(1));
                Assert.That(remainingTransfers[0].GetProperty("transferredBytes").GetInt64(), Is.EqualTo(4096));
                Assert.That(remainingTransfers[0].GetProperty("totalBytes").GetInt64(), Is.EqualTo(8192));
            });

            await controller.SignOutAsync();
            JsonElement signedOutTransfers = await ReadDiagnosticsRootAsync(controller, "currentTransfers");
            JsonElement signedOutRunProgress = await ReadDiagnosticsRootAsync(controller, "aggregateRunProgress");
            Assert.Multiple(() =>
            {
                Assert.That(signedOutTransfers.GetArrayLength(), Is.Zero);
                Assert.That(signedOutRunProgress.GetArrayLength(), Is.Zero);
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_DuringActionRequiredCapturesErrorWithoutPrivateDetails()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            syncPair.LocalRootPath = Path.Combine(_tempDirectory, "Cloud");
            Directory.CreateDirectory(syncPair.LocalRootPath);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                syncPairStore: syncPairStore);
            const string actionRequiredMessage =
                "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100.";

            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPair.Id,
                        syncPair.DisplayName,
                        SyncPairRunState.Error,
                        "Action required: " + actionRequiredMessage,
                        actionRequiredMessage,
                        DateTime.UtcNow),
                ],
                DateTime.UtcNow));

            string archivePath = await controller.ExportDiagnosticsAsync();
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            JsonElement exportedPair = document.RootElement.GetProperty("syncPairs")[0];

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(archivePath), Is.True);
                Assert.That(exportedPair.GetProperty("status").GetString(), Is.EqualTo("Error"));
                Assert.That(exportedPair.GetProperty("lastError").GetString(), Is.EqualTo("[sync-pair-error]"));
                Assert.That(diagnosticsJson, Does.Not.Contain(actionRequiredMessage));
            });
        }

        [Test]
        public async Task RemoveSyncPairAsync_MarksZeroPairBackgroundInactiveAfterLastPairDeletion()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.SyncPairStore = syncPairStore;
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);

            JsonElement beforeRemove = await ReadSyncLifecycleDiagnosticsAsync(controller);
            await controller.RemoveSyncPairAsync(syncPair.Id);
            JsonElement afterRemove = await ReadSyncLifecycleDiagnosticsAsync(controller);

            Assert.Multiple(() =>
            {
                Assert.That(beforeRemove.GetProperty("status").GetString(), Is.EqualTo("configuredPairs"));
                Assert.That(beforeRemove.GetProperty("syncPairCount").GetInt32(), Is.EqualTo(1));
                Assert.That(beforeRemove.GetProperty("isBackgroundActive").GetBoolean(), Is.True);
                Assert.That(afterRemove.GetProperty("status").GetString(), Is.EqualTo("zeroPairBackgroundInactive"));
                Assert.That(afterRemove.GetProperty("syncPairCount").GetInt32(), Is.Zero);
                Assert.That(afterRemove.GetProperty("syncCoreState").GetString(), Is.EqualTo("stopped"));
                Assert.That(afterRemove.GetProperty("isBackgroundActive").GetBoolean(), Is.False);
                Assert.That(host.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.DeletedSyncPairId, Is.EqualTo(syncPair.Id));
            });
        }

    }
}
