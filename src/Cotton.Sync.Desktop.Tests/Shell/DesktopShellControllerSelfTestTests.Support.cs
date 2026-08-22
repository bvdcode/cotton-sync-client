// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerSelfTestTests
    {
        private DesktopShellController CreateController(
            Func<DesktopTokenStorageCapabilitySnapshot>? tokenStorageCapabilities = null,
            IDesktopUpdateService? updateService = null,
            IDesktopUpdateInstaller? updateInstaller = null)
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            return CreateController(
                paths,
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                tokenStorageCapabilities,
                updateService: updateService,
                updateInstaller: updateInstaller);
        }

        private static DesktopShellController CreateController(
            DesktopAppPaths paths,
            SqliteSyncPairSettingsStore syncPairStore,
            Func<DesktopTokenStorageCapabilitySnapshot>? tokenStorageCapabilities = null,
            IAutostartService? autostartService = null,
            TimeSpan? serverProbeTimeout = null,
            IDesktopUpdateService? updateService = null,
            IDesktopUpdateInstaller? updateInstaller = null)
        {
            DesktopTraceLoggerFactory loggerFactory = new DesktopTraceLoggerFactory();
            return new DesktopShellController(
                paths,
                new DesktopSyncApplicationFactory(paths, loggerFactory),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                syncPairStore,
                new FakePlatformCommandService(),
                autostartService ?? new FakeAutostartService(),
                new DesktopShellControllerOptions
                {
                    TokenStorageCapabilities = tokenStorageCapabilities,
                    ServerProbeTimeout = serverProbeTimeout,
                    UpdateService = updateService,
                    UpdateInstaller = updateInstaller,
                });
        }

        private static DesktopUpdateCheckResult CreateUpdateCheckResult(bool isUpdateAvailable)
        {
            DesktopSemanticVersion latestVersion = DesktopSemanticVersion.Parse(isUpdateAvailable ? "0.0.2" : "0.0.1");
            DesktopReleaseManifest manifest = CreateReleaseManifest(latestVersion.ToString());
            return new DesktopUpdateCheckResult(
                manifest,
                DesktopSemanticVersion.Parse("0.0.1"),
                latestVersion,
                isUpdateAvailable,
                manifest.Assets[0]);
        }

        private static DesktopUpdateDownloadResult CreateUpdateDownloadResult(string installerPath)
        {
            DesktopReleaseManifest manifest = CreateReleaseManifest("0.0.2");
            return new DesktopUpdateDownloadResult(
                manifest,
                manifest.Assets[0],
                installerPath,
                manifest.Assets[0].Sha256,
                manifest.Assets[0].SizeBytes);
        }

        private static DesktopReleaseManifest CreateReleaseManifest(string version)
        {
            return new DesktopReleaseManifest(
                1,
                "Cotton Sync",
                version,
                "v" + version,
                "0123456789abcdef",
                "main",
                new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v" + version),
                [
                    new DesktopReleaseAsset(
                        "CottonSync-Windows-Setup.exe",
                        new string('a', 64),
                        1024,
                        new Uri("https://github.com/bvdcode/cotton-sync-client/releases/download/v" + version + "/CottonSync-Windows-Setup.exe")),
                ]);
        }

        private SyncPairSettings CreateSyncPair(bool isEnabled)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = Path.Combine(_tempDirectory, "Documents"),
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            };
        }

        private static SyncStateEntry[] CreateLargePlaceholderStateEntries(string syncPairId)
        {
            byte[] placeholderIdentity = Enumerable.Range(0, 16 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            return Enumerable.Range(0, 512)
                .Select(index => new SyncStateEntry
                {
                    SyncPairId = syncPairId,
                    RelativePath = "Large/file-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
                    Kind = SyncEntryKind.File,
                    RemoteFileId = Guid.NewGuid(),
                    RemoteContentHash = "hash-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    RemoteETag = "etag-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    PlaceholderIdentity = placeholderIdentity,
                    PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                })
                .ToArray();
        }

        private static string ReadEntry(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException(name + " was not found.");
            using Stream stream = entry.Open();
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private class SlowServerInfoEndpoint : IAsyncDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly CancellationTokenSource _cancellation = new();
            private readonly TimeSpan _delay;
            private readonly Task _listenTask;

            public SlowServerInfoEndpoint(TimeSpan delay)
            {
                _delay = delay;
                BaseAddress = new Uri("http://127.0.0.1:" + GetFreePort().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
                _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
                _listener.Start();
                _listenTask = Task.Run(HandleOneRequestAsync);
            }

            public Uri BaseAddress { get; }

            public bool ReceivedRequest { get; private set; }

            public async ValueTask DisposeAsync()
            {
                _cancellation.Cancel();
                _listener.Close();
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or OperationCanceledException)
                {
                }

                _cancellation.Dispose();
            }

            private async Task HandleOneRequestAsync()
            {
                HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token)
                    .ConfigureAwait(false);
                ReceivedRequest = true;
                await Task.Delay(_delay, _cancellation.Token).ConfigureAwait(false);
                byte[] payload = Encoding.UTF8.GetBytes("{\"product\":\"Cotton Cloud\",\"instanceIdHash\":\"test\"}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, _cancellation.Token).ConfigureAwait(false);
                context.Response.Close();
            }

            private static int GetFreePort()
            {
                using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }

        private class FakeAutostartService : IAutostartService
        {
            public bool IsSupported => true;

            public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }

            public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class ThrowingAutostartService : IAutostartService
        {
            private readonly Exception _exception;

            public ThrowingAutostartService(Exception exception)
            {
                _exception = exception;
            }

            public bool IsSupported => true;

            public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                throw _exception;
            }
        }

        private class FakePlatformCommandService : IPlatformCommandService
        {
            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FakeUpdateService : IDesktopUpdateService
        {
            private readonly DesktopUpdateCheckResult _checkResult;
            private readonly DesktopUpdateDownloadResult? _downloadResult;
            private readonly Exception? _checkException;

            public FakeUpdateService(
                DesktopUpdateCheckResult checkResult,
                DesktopUpdateDownloadResult? downloadResult = null,
                Exception? checkException = null)
            {
                _checkResult = checkResult;
                _downloadResult = downloadResult;
                _checkException = checkException;
            }

            public int CheckCalls { get; private set; }

            public int DownloadCalls { get; private set; }

            public Task<DesktopUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckCalls++;
                if (_checkException is not null)
                {
                    return Task.FromException<DesktopUpdateCheckResult>(_checkException);
                }

                return Task.FromResult(_checkResult);
            }

            public Task<DesktopUpdateDownloadResult> DownloadInstallerAsync(
                DesktopUpdateCheckResult checkResult,
                IProgress<DesktopUpdateDownloadProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadCalls++;
                return Task.FromResult(_downloadResult ?? throw new InvalidOperationException("No fake download result."));
            }
        }

        private class FakeUpdateInstaller : IDesktopUpdateInstaller
        {
            public string? InstallerPath { get; private set; }

            public bool? LaunchAfterUpdate { get; private set; }

            public DesktopUpdateInstallResult Result { get; set; } = new(42, false, null);

            public Exception? Exception { get; set; }

            public DesktopUpdateInstallResult StartSilentInstall(
                string installerPath,
                bool launchAfterUpdate)
            {
                InstallerPath = installerPath;
                LaunchAfterUpdate = launchAfterUpdate;
                if (Exception is not null)
                {
                    throw Exception;
                }

                return Result;
            }
        }
    }
}
