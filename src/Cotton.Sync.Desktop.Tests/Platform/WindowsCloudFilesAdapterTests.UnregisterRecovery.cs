// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public void UnregisterSyncRoot_RecoversInvalidNativeOperationWhenStorageProviderConfirmsAbsence()
        {
            const int HResultCloudFileInvalidOperation = unchecked((int)0x8007017C);
            List<string> operations = new List<string>();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                OperationLog = operations,
                UnregisterException = new WindowsCloudFilesNativeException(
                    "CfUnregisterSyncRoot",
                    HResultCloudFileInvalidOperation),
            };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar(operations);
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                storageProviderRegistrar: storageProvider,
                diagnostics: diagnostics);
            SyncPairSettings syncPair = CreateSyncPair(Path.Combine(_tempDirectory, "root"));

            adapter.UnregisterSyncRoot(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(
                    operations,
                    Is.EqualTo(new[] { "native-unregister", "storage-provider-unregister" }));
                Assert.That(storageProvider.IsRegistered(syncPair.Id), Is.False);
                Assert.That(
                    diagnostics.Snapshot().Select(static item => item.Status),
                    Is.EqualTo(new[] { "failed", "recovered", "completed" }));
            });
        }

        [Test]
        public void UnregisterSyncRoot_RejectsInvalidNativeOperationWhenStorageProviderRemainsRegistered()
        {
            const int HResultCloudFileInvalidOperation = unchecked((int)0x8007017C);
            List<string> operations = new List<string>();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                OperationLog = operations,
                UnregisterException = new WindowsCloudFilesNativeException(
                    "CfUnregisterSyncRoot",
                    HResultCloudFileInvalidOperation),
            };
            FakeStorageProviderSyncRootRegistrar storageProvider = new FakeStorageProviderSyncRootRegistrar(operations)
            {
                KeepRegistrationAfterUnregister = true,
            };
            SyncPairSettings syncPair = CreateSyncPair(Path.Combine(_tempDirectory, "root"));
            storageProvider.Registrations.Add(new WindowsStorageProviderSyncRootRegistration(
                syncPair.Id,
                syncPair.LocalRootPath,
                "1.0.0",
                "icon"));
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                storageProviderRegistrar: storageProvider);

            WindowsCloudFilesNativeException? exception =
                Assert.Throws<WindowsCloudFilesNativeException>(() => adapter.UnregisterSyncRoot(syncPair));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.HResult, Is.EqualTo(HResultCloudFileInvalidOperation));
                Assert.That(storageProvider.IsRegistered(syncPair.Id), Is.True);
            });
        }
    }
}
