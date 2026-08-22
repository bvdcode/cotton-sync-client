// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public async Task FinalizeUploadedFilePlaceholder_NotifiesExplorerAfterUploadedFileStatusFinalization()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            RecordingShellChangeNotifier shellChanges = new RecordingShellChangeNotifier();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                shellChangeNotifier: shellChanges,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string filePath = Path.GetFullPath(Path.Combine(root, "Projects", "report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "local");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/report.txt");

            await adapter.FinalizeUploadedFilePlaceholderAsync(syncPair, state);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { filePath }));
                Assert.That(shellChanges.ItemUpdates, Is.EqualTo(new[] { filePath }));
                Assert.That(shellChanges.DirectoryUpdates, Is.Empty);
            });
        }

        [TestCase(HResultSharingViolation)]
        [TestCase(HResultLockViolation)]
        public void FinalizeUploadedFilePlaceholder_MapsSharingViolationToExclusiveLocalFileWait(int hresult)
        {
            WindowsCloudFilesNativeException nativeFailure = new(
                "CfConvertToPlaceholder",
                hresult);
            FakeCloudFilesNativeApi nativeApi = new()
            {
                ConvertException = nativeFailure,
            };
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: _ => false);
            string root = Path.Combine(_tempDirectory, "root");
            string filePath = Path.GetFullPath(Path.Combine(root, "Projects", "open-in-excel.xlsx"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "workbook");
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/open-in-excel.xlsx");

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => adapter.FinalizeUploadedFilePlaceholderAsync(syncPair, state));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("Projects/open-in-excel.xlsx"));
                Assert.That(exception?.FullPath, Is.EqualTo(filePath));
                Assert.That(exception?.RequiresExclusiveAccess, Is.True);
                Assert.That(exception?.InnerException, Is.SameAs(nativeFailure));
                Assert.That(diagnostic.Operation, Is.EqualTo("finalize-uploaded-file-placeholder"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.HResult, Is.EqualTo(hresult));
            });
        }

        [Test]
        public void FinalizeUploadedFilePlaceholder_MapsExistingPlaceholderSharingViolationToExclusiveLocalFileWait()
        {
            WindowsCloudFilesNativeException nativeFailure = new(
                "CfUpdatePlaceholder",
                HResultSharingViolation);
            FakeCloudFilesNativeApi nativeApi = new()
            {
                ConvertException = nativeFailure,
            };
            WindowsCloudFilesDiagnostics diagnostics = new();
            string root = Path.Combine(_tempDirectory, "root");
            string filePath = Path.GetFullPath(Path.Combine(root, "Projects", "open-in-excel.xlsx"));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "workbook");
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
            SyncPairSettings syncPair = CreateSyncPair(root);
            SyncStateEntry state = CreateUploadedFileState(syncPair, "Projects/open-in-excel.xlsx");

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => adapter.FinalizeUploadedFilePlaceholderAsync(syncPair, state));

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("Projects/open-in-excel.xlsx"));
                Assert.That(exception?.FullPath, Is.EqualTo(filePath));
                Assert.That(exception?.RequiresExclusiveAccess, Is.True);
                Assert.That(exception?.InnerException, Is.SameAs(nativeFailure));
                Assert.That(diagnostic.Operation, Is.EqualTo("finalize-uploaded-file-placeholder"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.HResult, Is.EqualTo(HResultSharingViolation));
            });
        }

        [Test]
        public void TransferData_ForwardsToNativeBoundary()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            WindowsCloudFilesFetchDataRequest request = new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(3),
                [],
                5,
                0,
                5,
                0,
                5,
                null,
                0);
            WindowsCloudFilesTransferData transfer = WindowsCloudFilesTransferData.Success(
                request,
                Encoding.UTF8.GetBytes("hello"),
                0,
                5);

            adapter.TransferData(transfer);

            Assert.That(nativeApi.Transfers, Is.EqualTo(new[] { transfer }));
        }
    }
}
