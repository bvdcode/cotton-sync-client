// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopCommandLineRunnerTests
    {
        [Test]
        public async Task RunCloudFilesCleanupAsync_UnregistersOnlyVirtualFilesPairs()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteSyncPairSettingsStore store = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings fullMirror = CreateSyncPair("Full", SyncPairMode.FullMirror, Path.Combine(_tempDirectory, "full"));
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(fullMirror);
            await store.UpsertAsync(virtualFiles);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(paths.SyncStateDatabasePath);
            await stateStore.InitializeAsync();
            await stateStore.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = virtualFiles.Id.ToString("D"),
                LastCursor = 42,
                HasCompletedFullReconcile = true,
            });
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                paths,
                options,
                output,
                adapter,
                storageProvider);
            IReadOnlyList<SyncPairSettings> remainingPairs = await store.ListAsync();
            SyncChangeCursor cursor = await stateStore.GetChangeCursorAsync(virtualFiles.Id.ToString("D"));

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(remainingPairs.Select(static pair => pair.Id), Is.EquivalentTo(new[] { fullMirror.Id, virtualFiles.Id }));
                Assert.That(cursor.LastCursor, Is.EqualTo(42));
                Assert.That(cursor.HasCompletedFullReconcile, Is.False);
                Assert.That(output.ToString(), Does.Contain("Roots cleaned: 1"));
                Assert.That(output.ToString(), Does.Contain("Recovery queued: " + virtualFiles.LocalRootPath));
                Assert.That(output.ToString(), Does.Contain("Orphaned storage-provider roots cleaned."));
                Assert.That(output.ToString(), Does.Contain("Result: passed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenUnregisterFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            SqliteSyncPairSettingsStore store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            SyncPairSettings virtualFiles = CreateSyncPair("Virtual", SyncPairMode.WindowsVirtualFiles, Path.Combine(_tempDirectory, "virtual"));
            await store.UpsertAsync(virtualFiles);
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter
            {
                Exception = new InvalidOperationException("unregister failed"),
            };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar();
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { virtualFiles.Id }));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

        [Test]
        public async Task RunCloudFilesCleanupAsync_ReturnsFailureWhenOrphanedStorageProviderCleanupFails()
        {
            DesktopStartupOptions options = DesktopStartupOptions.Parse(["--data-dir", _tempDirectory, "--cleanup-cloud-files"]);
            SqliteSyncPairSettingsStore store = new SqliteSyncPairSettingsStore(DesktopAppPaths.CreateForDataDirectory(_tempDirectory).AppDatabasePath);
            await store.InitializeAsync();
            FakeCloudFilesAdapter adapter = new FakeCloudFilesAdapter();
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar
            {
                Exception = new InvalidOperationException("orphan cleanup failed"),
            };
            using StringWriter output = new StringWriter();

            int exitCode = await DesktopCommandLineRunner.RunCloudFilesCleanupAsync(
                DesktopAppPaths.CreateForDataDirectory(_tempDirectory),
                options,
                output,
                adapter,
                storageProvider);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(storageProvider.UnregisterAllCalls, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Failed orphaned storage-provider cleanup"));
                Assert.That(output.ToString(), Does.Contain("Failures: 1"));
                Assert.That(output.ToString(), Does.Contain("Result: failed"));
            });
        }

    }
}
