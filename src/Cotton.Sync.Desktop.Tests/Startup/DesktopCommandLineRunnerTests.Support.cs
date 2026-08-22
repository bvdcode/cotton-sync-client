// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopCommandLineRunnerTests
    {
        private static SyncPairSettings CreateSyncPair(string displayName, SyncPairMode mode, string localRootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                LocalRootPath = localRootPath,
                RemoteDisplayPath = "/" + displayName,
                RemoteRootNodeId = Guid.NewGuid(),
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private class FakeDesktopUpdateService : IDesktopUpdateService
        {
            public const long InstallerSizeBytes = 9;
            private const string LatestVersion = "0.1.1";
            private const string InstallerName = "CottonSync-Windows-Setup.exe";
            private static readonly string InstallerSha256 = new('a', 64);
            private readonly DesktopAppPaths _paths;
            private readonly DesktopReleaseManifest _manifest;
            private readonly DesktopReleaseAsset _installerAsset;

            public FakeDesktopUpdateService(DesktopAppPaths paths)
            {
                _paths = paths;
                _installerAsset = new DesktopReleaseAsset(
                    InstallerName,
                    InstallerSha256,
                    InstallerSizeBytes,
                    new Uri("https://updates.example/" + InstallerName));
                _manifest = new DesktopReleaseManifest(
                    1,
                    "Cotton Sync",
                    LatestVersion,
                    "v" + LatestVersion,
                    "0000000000000000000000000000000000000000",
                    "main",
                    new Uri("https://updates.example/releases/v" + LatestVersion),
                    [_installerAsset]);
            }

            public int CheckCalls { get; private set; }

            public int DownloadCalls { get; private set; }

            public Task<DesktopUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
            {
                CheckCalls++;
                return Task.FromResult(new DesktopUpdateCheckResult(
                    _manifest,
                    DesktopSemanticVersion.Parse("0.1.0"),
                    DesktopSemanticVersion.Parse(LatestVersion),
                    IsUpdateAvailable: true,
                    _installerAsset));
            }

            public async Task<DesktopUpdateDownloadResult> DownloadInstallerAsync(
                DesktopUpdateCheckResult checkResult,
                IProgress<DesktopUpdateDownloadProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadCalls++;
                string versionDirectory = Path.Combine(_paths.UpdateCacheDirectory, LatestVersion);
                Directory.CreateDirectory(versionDirectory);
                string installerPath = Path.Combine(versionDirectory, InstallerName);
                await File.WriteAllTextAsync(installerPath, "installer", cancellationToken).ConfigureAwait(false);
                return new DesktopUpdateDownloadResult(
                    checkResult.Manifest,
                    _installerAsset,
                    installerPath,
                    InstallerSha256,
                    InstallerSizeBytes);
            }
        }

        private class FakeDesktopShellShareLinkClient : IDesktopShellShareLinkClient
        {
            private readonly DesktopShellShareLinkResult _result;

            public FakeDesktopShellShareLinkClient(DesktopShellShareLinkResult result)
            {
                _result = result;
            }

            public Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
                ShellShareLinkTarget target,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_result);
            }
        }

        private class FakeDesktopClipboardService : IDesktopClipboardService
        {
            public string? CopiedText { get; private set; }

            public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
            {
                CopiedText = text;
                return Task.CompletedTask;
            }
        }

        private class FakeDesktopNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => true;

            public List<(string Title, string Message)> Messages { get; } = [];

            public void Show(string title, string message)
            {
                Messages.Add((title, message));
            }
        }

        private class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<SyncPairSettings> UnregisteredPairs { get; } = [];

            public Exception? Exception { get; set; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                UnregisteredPairs.Add(syncPair);
                if (Exception is not null)
                {
                    throw Exception;
                }
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private class FakeStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
        {
            public int UnregisterAllCalls { get; private set; }

            public Exception? Exception { get; set; }

            public bool IsSupported()
            {
                return true;
            }

            public bool IsRegistered(Guid syncPairId)
            {
                throw new NotSupportedException();
            }

            public void Register(WindowsStorageProviderSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void Unregister(Guid syncPairId, string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void UnregisterAllForCurrentUser()
            {
                UnregisterAllCalls++;
                if (Exception is not null)
                {
                    throw Exception;
                }
            }
        }

        private class FakeDesktopUpdateInstaller : IDesktopUpdateInstaller
        {
            public int Calls { get; private set; }

            public string? InstallerPath { get; private set; }

            public bool? LaunchAfterUpdate { get; private set; }

            public DesktopUpdateInstallResult StartSilentInstall(
                string installerPath,
                bool launchAfterUpdate)
            {
                Calls++;
                InstallerPath = installerPath;
                LaunchAfterUpdate = launchAfterUpdate;
                return new DesktopUpdateInstallResult(42, true, 0);
            }
        }
    }
}
