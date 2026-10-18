// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root and writes large local files.")]
        public async Task FinalizeUploadedFile_PreservesLargePinnedFile(bool interleaveAllocations)
        {
            const FileAttributes pinnedAttribute = (FileAttributes)0x00080000;
            const string fileName = "Учебное видео с длинным названием для проверки метаданных.mp4";
            TestContext.Out.WriteLine(RuntimeInformation.OSDescription);
            foreach (string systemFile in new[] { "cldapi.dll", "drivers/cldflt.sys", "drivers/ntfs.sys" })
            {
                string version = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, systemFile)).FileVersion!;
                TestContext.Out.WriteLine($"{systemFile}: {version}");
            }

            string root = Path.Combine(Path.GetTempPath(), "cotton-file-layout-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "Library", "Lessons", "Video", "2022");
            string filePath = Path.Combine(directory, fileName);
            string relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            Guid pairId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(directory);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                pairId, root, "file-layout-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "file-layout-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, rootId)));
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                string expectedHash = await CreateLargeFileAsync(filePath, interleaveAllocations);
                nativeApi.SetPinState(filePath, WindowsCloudFilesPinState.Pinned);
                FileInfo file = new(filePath);
                long size = file.Length;
                DateTime createdAt = file.CreationTimeUtc;
                DateTime lastWrite = file.LastWriteTimeUtc;
                byte[] identity = new WindowsCloudFilesPlaceholderIdentity(
                    WindowsCloudFilesPlaceholderIdentity.CurrentSchema,
                    WindowsCloudFilesAdapter.ProviderId,
                    pairId, rootId, relativePath,
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    size, expectedHash, "\"file-layout-version\"", lastWrite).ToBytes();
                string layout = await DesktopPowerShellFileReader.ReadAsync(
                    "& fsutil file layout /v $env:COTTON_SYNC_EXTERNAL_READ_PATH; "
                    + "if ($LASTEXITCODE -ne 0) { throw 'Reading the test file layout failed.' }",
                    filePath, TimeSpan.FromSeconds(30), CancellationToken.None);
                TestContext.Out.WriteLine($"interleaveAllocations={interleaveAllocations}; identityBytes={identity.Length}");
                TestContext.Out.WriteLine(layout);
                if (interleaveAllocations)
                {
                    Assert.That(layout, Does.Contain("::$ATTRIBUTE_LIST"),
                        "The fragmented fixture must span multiple NTFS file records before conversion.");
                }

                Assert.Multiple(() =>
                {
                    Assert.That(file.Attributes.HasFlag(pinnedAttribute), Is.True);
                    Assert.That(file.Attributes.HasFlag(FileAttributes.SparseFile), Is.False);
                    Assert.That(file.Attributes.HasFlag(FileAttributes.ReparsePoint), Is.False);
                });

                WindowsCloudFilesUploadedFileFinalizationResult result = await nativeApi.FinalizeUploadedFileAsync(
                    new WindowsCloudFilesUploadedFileFinalizationRequest(
                        new WindowsCloudFilesNativePlaceholder(directory, fileName, identity, size, createdAt, lastWrite),
                        expectedHash, size, lastWrite, WindowsCloudFilesUploadedFileFinalizationMode.ConvertRegularFile));
                await using FileStream reader = File.OpenRead(filePath);
                string actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(reader));
                Assert.Multiple(() =>
                {
                    Assert.That(result.IsFinalized, Is.True);
                    Assert.That(actualHash, Is.EqualTo(expectedHash));
                    Assert.That(reader.Length, Is.EqualTo(size));
                    Assert.That(nativeApi.GetPlaceholderIdentity(filePath), Is.EqualTo(identity));
                    Assert.That(nativeApi.GetPlaceholderState(filePath).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(File.GetAttributes(filePath).HasFlag(pinnedAttribute), Is.True);
                    Assert.That(File.GetCreationTimeUtc(filePath), Is.EqualTo(createdAt));
                    Assert.That(File.GetLastWriteTimeUtc(filePath), Is.EqualTo(lastWrite));
                });
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pairId, root);
                Directory.Delete(root, recursive: true);
            }
        }

        private static async Task<string> CreateLargeFileAsync(string filePath, bool interleaveAllocations)
        {
            const int blockSize = 4 * 1024 * 1024;
            const int blockCount = 128;
            byte[] block = RandomNumberGenerator.GetBytes(blockSize);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int index = 0; index < blockCount; index++)
            {
                await using (FileStream writer = new(filePath, new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                }))
                {
                    await writer.WriteAsync(block);
                    await writer.FlushAsync();
                }

                hash.AppendData(block);
                if (interleaveAllocations)
                {
                    await File.WriteAllBytesAsync(filePath + ".allocation-" + index, block);
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
}
