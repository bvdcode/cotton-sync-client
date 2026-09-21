// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task HydratePlaceholder_WhileExternalProcessReads_ReturnsCompleteContent(bool differentFile, bool externalReadFirst)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-concurrent-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                pairId, root, "concurrent-hydration-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "concurrent-hydration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, Guid.NewGuid())));
            byte[] content = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 123);
            string hash = Convert.ToHexString(SHA256.HashData(content));
            ConcurrentHydrationContentProvider provider = new(content, waitForConcurrentDownloads: differentFile);
            WindowsCloudFilesDiagnostics diagnostics = new(2000);
            WindowsCloudFilesHydrationCoordinator handler = new(provider, nativeApi, diagnostics: diagnostics);
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(new(root, handler));
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
                string path = Path.Combine(root, "example.bin");
                WindowsCloudFilesPlaceholderIdentity identity = new(
                    1, WindowsCloudFilesAdapter.ProviderId, pairId, Guid.NewGuid(), "example.bin",
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, content.Length, hash, null, timestamp);
                nativeApi.CreatePlaceholder(new(root, "example.bin", identity.ToBytes(), content.Length, timestamp, timestamp));
                string readPath = path;
                if (differentFile)
                {
                    readPath = Path.Combine(root, "second.bin");
                    WindowsCloudFilesPlaceholderIdentity secondIdentity = identity with
                    {
                        RelativePath = "second.bin",
                        NodeFileId = Guid.NewGuid(),
                    };
                    nativeApi.CreatePlaceholder(new(root, "second.bin", secondIdentity.ToBytes(), content.Length, timestamp, timestamp));
                }
                nativeApi.SetPinState(path, WindowsCloudFilesPinState.Pinned);
                Task hydration;
                Task<string> read;
                if (externalReadFirst)
                {
                    read = ReadExternallyAsync(readPath);
                    await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    hydration = Task.Run(() => nativeApi.HydratePlaceholder(path));
                }
                else
                {
                    hydration = Task.Run(() => nativeApi.HydratePlaceholder(path));
                    await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    read = ReadExternallyAsync(readPath);
                }
                await Task.WhenAll(hydration, read).WaitAsync(TimeSpan.FromSeconds(40));
                Assert.That((await read).Trim(), Is.EqualTo(hash));
                if (differentFile)
                {
                    Assert.That((await ReadExternallyAsync(path)).Trim(), Is.EqualTo(hash));
                }
            }
            finally
            {
                foreach (WindowsCloudFilesDiagnosticEvent item in diagnostics.Snapshot())
                {
                    TestContext.Out.WriteLine($"{item.Operation} {item.Status}: {item.Details}");
                }
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pairId, root);
                Directory.Delete(root, recursive: true);
            }
        }

        private static Task<string> ReadExternallyAsync(string path)
        {
            return DesktopPowerShellFileReader.ReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$bytes=[IO.File]::ReadAllBytes($env:COTTON_SYNC_EXTERNAL_READ_PATH); "
                + "$sha=[Security.Cryptography.SHA256]::Create(); "
                + "try { [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','') } finally { $sha.Dispose() }",
                path, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }
}
