// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Composition;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [TestCase("sync")]
        [TestCase("resume")]
        [TestCase("enable")]
        public async Task StartupFailure_PreservesEnabledSettingAndUserCommandRetries(string command)
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteSyncPairSettingsStore store = new(paths.AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings pair = CreateSyncPair(isEnabled: true);
            pair.LocalRootPath = _tempDirectory;
            await store.UpsertAsync(pair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.StartSyncException = new IOException("Cloud Files connection failed.");
            host.StatusPublisher.Publish(new SyncAppStatus(true,
                [new SyncPairStatus(pair.Id, pair.DisplayName, SyncPairRunState.Stopped, null, null, DateTime.UtcNow)], DateTime.UtcNow));
            using DesktopShellController controller = CreateController(paths,
                new QueueingDesktopSyncApplicationFactory(host.Host), syncPairStore: store);
            TaskCompletionSource<DesktopSyncPairStatusSnapshot> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.StatusChanged += (_, status) =>
            {
                DesktopSyncPairStatusSnapshot? item = status.SyncPairs.FirstOrDefault(item => item.Status == "Error");
                if (item is not null)
                {
                    failure.TrySetResult(item);
                }
            };

            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            DesktopSyncPairStatusSnapshot failedPair = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(failedPair.LastError, Does.Contain("Sync could not start."));
            Assert.That((await store.GetAsync(pair.Id))!.IsEnabled, Is.True);
            JsonElement failedLifecycle = await ReadSyncLifecycleDiagnosticsAsync(controller);
            Assert.That(failedLifecycle.GetProperty("syncCoreState").GetString(), Is.EqualTo("startFailed"));

            host.App.StartSyncException = null;
            switch (command)
            {
                case "sync":
                    await controller.SyncAllAsync();
                    break;
                case "resume":
                    await controller.ResumeAllAsync();
                    break;
                case "enable":
                    await controller.SetSyncPairEnabledAsync(pair.Id, true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }

            JsonElement recoveredLifecycle = await ReadSyncLifecycleDiagnosticsAsync(controller);
            Assert.Multiple(() =>
            {
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(2));
                Assert.That(recoveredLifecycle.GetProperty("syncCoreState").GetString(), Is.EqualTo("running"));
            });
        }
    }
}
