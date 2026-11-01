// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Platform(Include = "Win")]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task RunOnceAsync_AccountsForNativeExternalDehydration(bool startupRecovery)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-availability-" + Guid.NewGuid().ToString("N"));
            string filePath = Path.Combine(root, "video.mp4");
            byte[] content = RandomNumberGenerator.GetBytes(65539);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            SyncPairSettings pair = CreateVirtualFilesPair();
            pair.Id = Guid.NewGuid();
            pair.LocalRootPath = root;
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(WindowsCloudFilesNativeApiIntegrationTests.GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator handler = new(
                new WindowsCloudFilesHydrationCoordinatorTests.FakeContentProvider(content), nativeApi,
                diagnostics: diagnostics);
            Directory.CreateDirectory(root);
            try
            {
                using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(pair, handler);
                await File.WriteAllBytesAsync(filePath, content);
                SyncStateEntry state = CreatePlaceholderState(pair, "video.mp4");
                state.RemoteFileManifestId = Guid.NewGuid();
                state.RemoteContentHash = contentHash;
                state.LocalContentHash = contentHash;
                state.RemoteSizeBytes = content.Length;
                state.LocalSizeBytes = content.Length;
                state.LocalLastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                RemoteFilePlaceholderResult finalized = await adapter.FinalizeUploadedFilePlaceholderAsync(pair, state);
                state.PlaceholderIdentity = finalized.PlaceholderIdentity;
                state.PlaceholderHydrationState = finalized.HydrationState;
                state.LocalSizeBytes = finalized.LocalSizeBytes;
                state.LocalLastWriteUtc = finalized.LocalLastWriteUtc;
                FakeSyncStateStore store = new();
                store.UpsertEntry(state);
                nativeApi.SetPinState(filePath, WindowsCloudFilesPinState.Unspecified);
                nativeApi.DehydratePlaceholder(filePath);
                FileAttributes onlineAttributes = File.GetAttributes(filePath);
                Assert.That((onlineAttributes & (FileAttributes)(FileAttributePinned | FileAttributeUnpinned)), Is.EqualTo((FileAttributes)0));
                Assert.That((onlineAttributes & (FileAttributes.Offline | (FileAttributes)FileAttributeRecallOnDataAccess)), Is.Not.EqualTo((FileAttributes)0));

                LocalFileScanner scanner = new();
                LocalFileUnavailableException? denied = Assert.ThrowsAsync<LocalFileUnavailableException>(async () =>
                    await scanner.ComputeContentHashAsync(new LocalFileSnapshot
                    {
                        RelativePath = state.RelativePath, FullPath = filePath,
                        IsCloudFilesPlaceholder = true, IsCloudFilesOnlineOnlyPlaceholder = true,
                    }));
                Assert.That(denied!.InnerException!.HResult, Is.EqualTo(unchecked((int)0x8007018B)));
                RecordingSyncPairWork inner = new();
                WindowsVirtualFilesDehydrationPairWork work = new(
                    inner, store, adapter, new WindowsCloudFilesHydrator(adapter, handler), diagnostics: diagnostics);
                SyncRunRequest request = startupRecovery
                    ? SyncRunRequest.ForFull(SyncRunCause.Periodic)
                    : SyncRunRequest.ForLocalChangedPaths([state.RelativePath]);

                await work.RunOnceAsync(pair, request);

                Assert.Multiple(() =>
                {
                    Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                    Assert.That(state.LocalContentHash, Is.Null);
                    Assert.That(state.RemoteContentHash, Is.EqualTo(contentHash));
                    Assert.That(File.GetAttributes(filePath), Is.EqualTo(onlineAttributes));
                    Assert.That(nativeApi.GetPlaceholderIdentity(filePath), Is.EqualTo(state.PlaceholderIdentity));
                    Assert.That(inner.Requests.Count, Is.EqualTo(startupRecovery ? 1 : 0));
                    Assert.That(diagnostics.Snapshot().Any(item => item.Operation == "external-dehydration"), Is.True);
                    Assert.That(diagnostics.Snapshot().Any(item => item.Operation.StartsWith("hydrate", StringComparison.Ordinal)), Is.False);
                });

                await new WindowsCloudFilesHydrator(adapter, handler).HydratePlaceholderAsync(pair, state.RelativePath);
                byte[] restored = await File.ReadAllBytesAsync(filePath);
                Assert.That(restored, Is.EqualTo(content));
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pair.Id, root);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
