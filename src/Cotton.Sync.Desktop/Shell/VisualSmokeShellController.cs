// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;

namespace Cotton.Sync.Desktop.Shell
{
    internal class VisualSmokeShellController : IDesktopShellController
    {
        private static readonly TimeSpan DefaultProgressAnimationInterval = TimeSpan.FromMilliseconds(100);
        private readonly DesktopVisualSmokeScenario _scenario;
        private readonly VisualSmokeSnapshotFactory _snapshotFactory;
        private readonly VisualSmokeProgressAnimator _progressAnimator;

        private VisualSmokeShellController(
            DesktopVisualSmokeScenario scenario,
            TimeSpan progressAnimationInterval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(progressAnimationInterval, TimeSpan.Zero);

            _scenario = scenario;
            _snapshotFactory = new VisualSmokeSnapshotFactory(scenario);
            _progressAnimator = new VisualSmokeProgressAnimator(scenario, progressAnimationInterval);
        }

        public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged
        {
            add => _progressAnimator.StatusChanged += value;
            remove => _progressAnimator.StatusChanged -= value;
        }

        public event EventHandler<DesktopActivitySnapshot>? ActivityReported
        {
            add { }
            remove { }
        }

        public event EventHandler<DesktopSessionRevocationSnapshot>? SessionRevoked
        {
            add { }
            remove { }
        }

        public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged
        {
            add => _progressAnimator.TransferProgressChanged += value;
            remove => _progressAnimator.TransferProgressChanged -= value;
        }

        public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged
        {
            add => _progressAnimator.RunProgressChanged += value;
            remove => _progressAnimator.RunProgressChanged -= value;
        }

        public static VisualSmokeShellController Create(DesktopVisualSmokeScenario scenario)
        {
            return Create(scenario, DefaultProgressAnimationInterval);
        }

        internal static VisualSmokeShellController Create(
            DesktopVisualSmokeScenario scenario,
            TimeSpan progressAnimationInterval)
        {
            return new VisualSmokeShellController(scenario, progressAnimationInterval);
        }

        public Task<DesktopShellSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesktopShellSnapshot snapshot = _snapshotFactory.CreateShellSnapshot(DateTime.UtcNow.AddMinutes(-7));
            _progressAnimator.Start();
            return Task.FromResult(snapshot);
        }

        public Task<DesktopServerProbeResult> ProbeServerAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = new Uri("https://app.cottoncloud.dev/");
            return Task.FromResult(new DesktopServerProbeResult(url, true, "Cotton Cloud", "visual-smoke"));
        }

        public Task<DesktopStoredSessionRestoreSnapshot> RestoreStoredSessionAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthSession session = new(
                Guid.Parse("7ab1a10f-5fa8-4e4e-8d4d-db3ea720aeef"),
                "qa@cottoncloud.dev",
                "qa@cottoncloud.dev",
                isTotpEnabled: true);
            return Task.FromResult(new DesktopStoredSessionRestoreSnapshot(session, true, null));
        }

        public Task<AuthSession> SignInAsync(
            DesktopSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AuthSession(
                Guid.Parse("7ab1a10f-5fa8-4e4e-8d4d-db3ea720aeef"),
                "qa@cottoncloud.dev",
                "qa@cottoncloud.dev",
                isTotpEnabled: true));
        }

        public Task<AuthSession> SignInWithBrowserAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AuthSession(
                Guid.Parse("7ab1a10f-5fa8-4e4e-8d4d-db3ea720aeef"),
                "qa@cottoncloud.dev",
                "qa@cottoncloud.dev",
                isTotpEnabled: true));
        }

        public Task<DesktopRemoteFolderListSnapshot> ListRemoteFoldersAsync(
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshotFactory.ListRemoteFolders(remotePath));
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<SyncPairSettings> AddSyncPairAsync(
            DesktopSyncPairRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "New folder",
                LocalRootPath = request.LocalFolderPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = request.RemoteFolderPath,
                IsEnabled = true,
                Mode = request.Mode,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        public Task SetSyncPairEnabledAsync(Guid syncPairId, bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RenameSyncPairAsync(Guid syncPairId, string displayName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SyncAllAsync(
            CancellationToken cancellationToken = default,
            Guid? syncPairId = null,
            RemoteDeletePlanApproval? approvedRemoteDeletePlan = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PauseAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task OpenWebAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SetStartWithOperatingSystemAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SetNotificationsEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SetThemeModeAsync(AppThemeMode themeMode, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DesktopSelfTestSnapshot> RunSelfTestAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(VisualSmokeSnapshotFactory.CreateSelfTestSnapshot());
        }

        public Task<DesktopUpdateStatusSnapshot> CheckForUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Manual,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopUpdateStatusSnapshot(
                DesktopAppVersion.Current,
                DesktopAppVersion.Current,
                false,
                false,
                "Cotton Sync is up to date.",
                null,
                null));
        }

        public Task<DesktopUpdateStatusSnapshot> DownloadUpdateAsync(
            DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Download,
            IProgress<DesktopUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CheckForUpdateAsync(cancellationToken: cancellationToken);
        }

        public Task<DesktopUpdateInstallResult> InstallDownloadedUpdateAsync(string installerPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopUpdateInstallResult(42, false, null));
        }

        public Task<string> ExportDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            return ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken);
        }

        public Task<string> ExportDiagnosticsAsync(
            DesktopDiagnosticsExportOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke-diagnostics.zip"));
        }

        public Task<DesktopRemoteFolderSnapshot> CreateRemoteFolderAsync(
            string parentPath,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedParent = string.IsNullOrWhiteSpace(parentPath) ? "/" : parentPath;
            string normalizedName = folderName.Trim();
            string path = normalizedParent == "/"
                ? "/" + normalizedName
                : normalizedParent.TrimEnd('/') + "/" + normalizedName;
            return Task.FromResult(new DesktopRemoteFolderSnapshot(Guid.NewGuid(), normalizedName, path));
        }

        public void Dispose()
        {
            _progressAnimator.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _progressAnimator.DisposeAsync().ConfigureAwait(false);
        }








    }
}
