// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        private static DesktopShellController CreateController(
            DesktopAppPaths paths,
            IDesktopSyncApplicationFactory factory,
            Func<DesktopTokenStorageCapabilitySnapshot>? tokenStorageCapabilities = null,
            Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>>? tokenStorageVerifier = null,
            TimeSpan? tokenStorageVerificationTimeout = null,
            TimeSpan? savedSessionRestoreRetryBaseDelay = null,
            IAutostartService? autostartService = null,
            SqliteSyncPairSettingsStore? syncPairStore = null)
        {
            return new DesktopShellController(
                paths,
                factory,
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                syncPairStore ?? new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                new FakePlatformCommandService(),
                autostartService ?? new FakeAutostartService(),
                new DesktopShellControllerOptions
                {
                    TokenStorageCapabilities = tokenStorageCapabilities ?? CreateSecureTokenStorage,
                    TokenStorageVerifier = tokenStorageVerifier,
                    SavedSessionRestoreRetryBaseDelay = savedSessionRestoreRetryBaseDelay,
                    TokenStorageVerificationTimeout = tokenStorageVerificationTimeout,
                });
        }

        private static async Task<JsonElement> ReadSyncLifecycleDiagnosticsAsync(DesktopShellController controller)
        {
            return await ReadDiagnosticsRootAsync(controller, "syncLifecycle");
        }

        private static async Task<JsonElement> ReadDiagnosticsRootAsync(
            DesktopShellController controller,
            string propertyName)
        {
            string archivePath = await controller.ExportDiagnosticsAsync();
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.GetProperty(propertyName).Clone();
        }

        private static string ReadEntry(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(
                "Diagnostics archive entry is missing: " + entryName);
            using Stream stream = entry.Open();
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static SyncPairSettings CreateSyncPair(bool isEnabled)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static DesktopTokenStorageCapabilitySnapshot CreateSecureTokenStorage()
        {
            return new DesktopTokenStorageCapabilitySnapshot(
                "test-secure",
                IsReleaseSecure: true,
                "Test secure token storage");
        }

        private static DesktopTokenStorageCapabilitySnapshot CreateInsecureTokenStorage()
        {
            return new DesktopTokenStorageCapabilitySnapshot(
                "restricted-file-v1",
                IsReleaseSecure: false,
                "Development fallback");
        }

        private class QueueingDesktopSyncApplicationFactory : IDesktopSyncApplicationFactory
        {
            private readonly Queue<DesktopSyncApplicationHost> _hosts;

            public QueueingDesktopSyncApplicationFactory(params DesktopSyncApplicationHost[] hosts)
            {
                _hosts = new Queue<DesktopSyncApplicationHost>(hosts);
            }

            public List<Uri> CreatedServerUrls { get; } = [];

            public DesktopSyncApplicationHost Create(Uri serverUrl)
            {
                CreatedServerUrls.Add(serverUrl);
                return _hosts.Dequeue();
            }
        }
    }
}
