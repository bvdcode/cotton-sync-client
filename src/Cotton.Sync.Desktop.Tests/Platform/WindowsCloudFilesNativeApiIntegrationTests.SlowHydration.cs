// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root and downloads for more than one minute.")]
        public async Task HydratePlaceholder_DownloadExceedsNativeTimeout_ReportsProgressAndCompletes()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-slow-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new(pairId, root, "slow-hydration-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new(root, WindowsCloudFilesAdapter.ProviderName, "slow-hydration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, Guid.NewGuid())));
            byte[] content = RandomNumberGenerator.GetBytes(14 * 4096);
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator handler = new(new SlowHydrationContentProvider(content), nativeApi, diagnostics: diagnostics);
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(new(root, handler));
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
                string path = Path.Combine(root, "large-video.bin");
                WindowsCloudFilesPlaceholderIdentity identity = new(1, WindowsCloudFilesAdapter.ProviderId, pairId,
                    Guid.NewGuid(), "large-video.bin", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
                    content.Length, hash, null, timestamp);
                nativeApi.CreatePlaceholder(new(root, identity.RelativePath, identity.ToBytes(), content.Length, timestamp, timestamp));
                nativeApi.SetPinState(path, WindowsCloudFilesPinState.Pinned);
                Stopwatch elapsed = Stopwatch.StartNew();
                await Task.Run(() => nativeApi.HydratePlaceholder(path)).WaitAsync(TimeSpan.FromMinutes(2));
                elapsed.Stop();
                byte[] actualContent = await File.ReadAllBytesAsync(path);
                Assert.Multiple(() =>
                {
                    Assert.That(elapsed.Elapsed, Is.GreaterThan(TimeSpan.FromSeconds(60)));
                    Assert.That(actualContent, Is.EqualTo(content));
                    Assert.That(nativeApi.GetPlaceholderState(path).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(diagnostics.Snapshot().Any(static item => item.Status == "failed"), Is.False);
                });
                TestContext.Out.WriteLine($"Downloaded and verified {content.Length} bytes over {elapsed.Elapsed.TotalSeconds:F1} seconds.");
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
    }
}
