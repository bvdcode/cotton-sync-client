// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class DesktopCloudFilesCapabilities
    {
        private static readonly Guid SelfTestRemoteRootNodeId =
            Guid.Parse("f8c8b2b5-39f4-4d60-a4c3-09e7c01bde12");
        private static readonly Guid SelfTestSyncPairId =
            Guid.Parse("728a21a6-4ed6-40a8-8942-d95d5d49d9ff");
        private const string SelfTestProbeRootName = "cotton-cloud-files-self-test";
        private const string SelfTestPlaceholderPath = "cloud-files-self-test-placeholder.txt";

        public static SyncPairModeCapabilitySnapshot CreateSyncPairModeCapabilities()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows virtual files require the Windows Cloud Files API.");
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows virtual files require Windows 10 version 1803 or newer.");
            }

            WindowsStorageProviderSyncRootRegistrar? storageProviderRegistrar =
                WindowsStorageProviderSyncRootRegistrar.TryCreateDefault();
            if (storageProviderRegistrar is null)
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows Cloud Files API is available, but the Cotton Sync Windows shell helper is not installed.");
            }

            try
            {
                if (!storageProviderRegistrar.IsSupported())
                {
                    return new SyncPairModeCapabilitySnapshot(
                        false,
                        "Windows Cloud Files API is available, but Windows StorageProvider sync-root registration is not supported on this device.");
                }
            }
            catch (Exception exception)
            {
                return new SyncPairModeCapabilitySnapshot(
                    false,
                    "Windows Cloud Files API is available, but Windows StorageProvider sync-root registration could not be verified: "
                    + exception.Message);
            }

            return new SyncPairModeCapabilitySnapshot(
                true,
                "Windows Cloud Files API, StorageProvider sync-root registration, and Explorer dehydration handling are available.");
        }

        internal static DesktopCloudFilesSelfTestCapabilitySnapshot CreateSelfTestCapability(
            SyncPairModeCapabilitySnapshot? basicCapabilities = null,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            Func<string>? createProbeRoot = null)
        {
            SyncPairModeCapabilitySnapshot basic = basicCapabilities ?? CreateSyncPairModeCapabilities();
            if (!basic.IsWindowsVirtualFilesSupported)
            {
                return new DesktopCloudFilesSelfTestCapabilitySnapshot(
                    false,
                    true,
                    basic.WindowsVirtualFilesDetails);
            }

            IWindowsCloudFilesAdapter cloudFiles = cloudFilesAdapter ?? new WindowsCloudFilesAdapter();
            string probeRoot = (createProbeRoot ?? CreateProbeRoot)();
            var syncPair = new SyncPairSettings
            {
                Id = SelfTestSyncPairId,
                DisplayName = "Cotton Sync self-test",
                LocalRootPath = probeRoot,
                RemoteDisplayPath = "/",
                RemoteRootNodeId = SelfTestRemoteRootNodeId,
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
            };
            WindowsCloudFilesConnection? connection = null;
            Exception? failure = null;
            Exception? cleanupFailure = null;

            try
            {
                Directory.CreateDirectory(probeRoot);
                cloudFiles.CreateFilePlaceholder(CreateSelfTestPlaceholderRequest(syncPair));
                connection = cloudFiles.ConnectSyncRoot(syncPair, NoopWindowsCloudFilesCallbackHandler.Instance);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    connection?.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                try
                {
                    cloudFiles.UnregisterSyncRoot(syncPair);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                try
                {
                    if (Directory.Exists(probeRoot))
                    {
                        Directory.Delete(probeRoot, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (failure is not null)
            {
                return new DesktopCloudFilesSelfTestCapabilitySnapshot(
                    false,
                    false,
                    "Windows Cloud Files API and StorageProvider sync-root registration are available, but "
                    + "placeholder creation and CfConnectSyncRoot could not be verified: "
                    + CleanSingleLine(failure.Message));
            }

            if (cleanupFailure is not null)
            {
                return new DesktopCloudFilesSelfTestCapabilitySnapshot(
                    false,
                    false,
                    "Windows Cloud Files sync-root connection succeeded, but self-test cleanup could not be verified: "
                    + CleanSingleLine(cleanupFailure.Message));
            }

            return new DesktopCloudFilesSelfTestCapabilitySnapshot(
                true,
                false,
                "Windows Cloud Files API, StorageProvider sync-root registration, placeholder creation, CfConnectSyncRoot, and cleanup are available.");
        }

        internal static string CreateProbeRoot()
        {
            return Path.Combine(
                Path.GetTempPath(),
                SelfTestProbeRootName);
        }

        private static string CleanSingleLine(string value)
        {
            return (string.IsNullOrWhiteSpace(value) ? "Operation could not be completed." : value)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static RemoteFilePlaceholderRequest CreateSelfTestPlaceholderRequest(SyncPairSettings syncPair)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SelfTestPlaceholderPath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("5e59d0ba-8d71-4cf1-9f00-d784cde8277c"),
                    NodeId = Guid.Parse("75cfa944-e2d4-48a6-a275-29a4710a92cb"),
                    FileManifestId = Guid.Parse("415f589e-8e5d-48df-8b34-677b63339cc8"),
                    OriginalNodeFileId = Guid.Parse("50b7c79d-8803-4c32-a2dc-e3c8a0921762"),
                    OwnerId = Guid.Parse("82195040-4dbe-44b0-8408-d5d337377db3"),
                    Name = SelfTestPlaceholderPath,
                    ContentType = "text/plain",
                    SizeBytes = 1,
                    ContentHash = "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881",
                    ETag = "cloud-files-self-test",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = SelfTestPlaceholderPath },
                });
        }

        private sealed class NoopWindowsCloudFilesCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public static NoopWindowsCloudFilesCallbackHandler Instance { get; } = new();

            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }
    }

    internal sealed record DesktopCloudFilesSelfTestCapabilitySnapshot(
        bool Passed,
        bool Skipped,
        string Details);
}
