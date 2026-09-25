// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Net.Http;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root and reads from a loopback HTTP server.")]
        public async Task HydratePlaceholder_InterruptedResponseBody_RetriesOnlyFileAndCompletes()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-interrupted-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new(pairId, root, "interrupted-hydration-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new(root, WindowsCloudFilesAdapter.ProviderName, "interrupted-hydration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, Guid.NewGuid())));
            byte[] content = RandomNumberGenerator.GetBytes(13 * 1024 * 1024 + 123);
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            await using InterruptedHydrationContentProvider provider = new(content);
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator handler = new(provider, nativeApi, diagnostics: diagnostics);
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(new(root, handler));
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
                string path = Path.Combine(root, "photo.raw");
                WindowsCloudFilesPlaceholderIdentity identity = new(1, WindowsCloudFilesAdapter.ProviderId, pairId,
                    Guid.NewGuid(), "photo.raw", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
                    content.Length, hash, null, timestamp);
                nativeApi.CreatePlaceholder(new(root, identity.RelativePath, identity.ToBytes(), content.Length, timestamp, timestamp));
                nativeApi.SetPinState(path, WindowsCloudFilesPinState.Pinned);

                await Task.Run(() => nativeApi.HydratePlaceholder(path)).WaitAsync(TimeSpan.FromSeconds(30));

                byte[] actualContent = await File.ReadAllBytesAsync(path);
                Assert.Multiple(() =>
                {
                    Assert.That(provider.Attempts, Is.EqualTo(2));
                    Assert.That(provider.InterruptedError, Is.EqualTo(HttpRequestError.ResponseEnded));
                    Assert.That(provider.RequestedPaths, Is.EqualTo(new[] { "photo.raw", "photo.raw" }));
                    Assert.That(actualContent, Is.EqualTo(content));
                    Assert.That(nativeApi.GetPlaceholderState(path).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(diagnostics.Snapshot().Any(static item => item.Status == "failed"), Is.False);
                });
                TestContext.Out.WriteLine($"Interrupted HTTP body recovered after {provider.Attempts} attempts; verified {actualContent.Length} bytes.");
            }
            finally
            {
                TestContext.Out.WriteLine($"HTTP attempts: {provider.Attempts}; interrupted error: {provider.InterruptedError}.");
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
