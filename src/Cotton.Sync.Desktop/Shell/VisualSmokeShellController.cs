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
        private static readonly Guid DocumentsPairId = Guid.Parse("8e40c25d-7a6d-4a8c-92cf-f7b5422a7e78");
        private static readonly Guid PhotosPairId = Guid.Parse("aa0c3835-2e86-4667-8bf9-81ce3bcd2bb8");
        private static readonly TimeSpan DefaultProgressAnimationInterval = TimeSpan.FromMilliseconds(100);
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly TimeSpan _progressAnimationInterval;
        private readonly DesktopVisualSmokeScenario _scenario;
        private Task? _progressAnimationTask;
        private int _progressAnimationStarted;
        private int _isDisposed;

        private VisualSmokeShellController(
            DesktopVisualSmokeScenario scenario,
            TimeSpan progressAnimationInterval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(progressAnimationInterval, TimeSpan.Zero);

            _scenario = scenario;
            _progressAnimationInterval = progressAnimationInterval;
        }

        public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged;

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

        public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged;

        public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged;

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
            DateTime syncedAt = DateTime.UtcNow.AddMinutes(-7);
            bool isSignedIn = _scenario is not DesktopVisualSmokeScenario.Connecting
                and not DesktopVisualSmokeScenario.SignInError;
            IReadOnlyList<DesktopSyncPairSnapshot> pairs = CreatePairs(syncedAt);

            var snapshot = new DesktopShellSnapshot(
                new Uri("https://app.cottoncloud.dev/"),
                isSignedIn ? "qa@cottoncloud.dev" : "Signed out",
                "qa@cottoncloud.dev",
                true,
                true,
                AppThemeMode.Dark,
                new DesktopDataPathSnapshot(
                    Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke"),
                    Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke", "sync-app.db"),
                    Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke", "sync-state.db"),
                    Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke", "tokens.json")),
                DesktopPlatformCapabilities.CreateSnapshot(),
                isSignedIn,
                pairs);
            StartProgressAnimation();
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
            string normalizedRemotePath = NormalizeRemotePath(remotePath);
            if (_scenario == DesktopVisualSmokeScenario.AddFolderManyRemoteFolders)
            {
                if (normalizedRemotePath != "/")
                {
                    return Task.FromResult(new DesktopRemoteFolderListSnapshot(normalizedRemotePath, []));
                }

                IReadOnlyList<DesktopRemoteFolderSnapshot> manyFolders = Enumerable.Range(1, 250)
                    .Select(index => new DesktopRemoteFolderSnapshot(
                        Guid.CreateVersion7(),
                        "Project archive " + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                        "/Project archive " + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();
                return Task.FromResult(new DesktopRemoteFolderListSnapshot("/", manyFolders));
            }

            if (normalizedRemotePath != "/")
            {
                return Task.FromResult(new DesktopRemoteFolderListSnapshot(normalizedRemotePath, []));
            }

            IReadOnlyList<DesktopRemoteFolderSnapshot> folders =
            [
                new DesktopRemoteFolderSnapshot(Guid.Parse("10a52979-ae72-42e6-8f05-c70b0a73cd20"), "Documents", "/Documents"),
                new DesktopRemoteFolderSnapshot(Guid.Parse("74b4732d-8d0b-4e39-b41b-99eb070c212f"), "Photos", "/Photos"),
                new DesktopRemoteFolderSnapshot(Guid.Parse("386f35fc-f1b7-492c-8fe0-c814144d1646"), "Projects", "/Projects"),
            ];
            return Task.FromResult(new DesktopRemoteFolderListSnapshot("/", folders));
        }

        private static string NormalizeRemotePath(string remotePath)
        {
            string normalized = string.IsNullOrWhiteSpace(remotePath)
                ? "/"
                : remotePath.Trim().Replace('\\', '/');
            if (!normalized.StartsWith('/'))
            {
                normalized = "/" + normalized;
            }

            return normalized.Length > 1
                ? normalized.TrimEnd('/')
                : "/";
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
            IReadOnlyList<DesktopSelfTestItemSnapshot> items =
            [
                new DesktopSelfTestItemSnapshot("Preferences database", true, "Writable"),
                new DesktopSelfTestItemSnapshot("Token storage", true, "Release-secure storage available"),
                new DesktopSelfTestItemSnapshot("Server identity", true, "Cotton Cloud"),
            ];
            return Task.FromResult(new DesktopSelfTestSnapshot(items));
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
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            _lifetimeCancellation.Cancel();
            try
            {
                if (_progressAnimationTask is not null)
                {
                    await _progressAnimationTask.ConfigureAwait(false);
                }
            }
            finally
            {
                _lifetimeCancellation.Dispose();
            }
        }

        private void StartProgressAnimation()
        {
            if ((_scenario is not DesktopVisualSmokeScenario.HydrationProgress
                    and not DesktopVisualSmokeScenario.DehydrationProgress)
                || Interlocked.Exchange(ref _progressAnimationStarted, 1) != 0)
            {
                return;
            }

            _progressAnimationTask = _scenario == DesktopVisualSmokeScenario.HydrationProgress
                ? AnimateHydrationProgressAsync(_lifetimeCancellation.Token)
                : AnimateDehydrationProgressAsync(_lifetimeCancellation.Token);
        }

        private async Task AnimateHydrationProgressAsync(CancellationToken cancellationToken)
        {
            const int displayedFiles = 50;
            const int totalFiles = 2000;
            const long totalBytes = 8_388_608_000;
            DateTime animationCompletedAtUtc = DateTime.UtcNow;
            DateTime startedAtUtc = animationCompletedAtUtc.AddSeconds(-(displayedFiles * 20));

            try
            {
                await DelayAnimationIntervalsAsync(30, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < displayedFiles; index++)
                {
                    int completedBefore = index * totalFiles / displayedFiles;
                    int completedAfter = (index + 1) * totalFiles / displayedFiles;
                    long completedBytesBefore = completedBefore * totalBytes / totalFiles;
                    long completedBytesAfter = completedAfter * totalBytes / totalFiles;
                    long currentFileBytes = 3_145_728 + ((index % 5) * 786_432);
                    string relativePath = "Music/Albums/Album "
                        + (index + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture)
                        + "/track-"
                        + completedAfter.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)
                        + ".flac";
                    DateTime occurredAtUtc = startedAtUtc.AddSeconds(index * 20);

                    RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                        DocumentsPairId,
                        SyncRunProgressStage.HydratingCloudFiles,
                        completedBefore,
                        totalFiles,
                        relativePath,
                        startedAtUtc,
                        IsCompleted: false,
                        occurredAtUtc,
                        completedBytesBefore,
                        totalBytes));
                    TransferProgressChanged?.Invoke(this, new DesktopTransferProgressSnapshot(
                        DocumentsPairId,
                        SyncTransferDirection.Download,
                        relativePath,
                        TransferredBytes: 0,
                        currentFileBytes,
                        IsCompleted: false,
                        occurredAtUtc,
                        SpeedBytesPerSecond: 8_388_608,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(1)));

                    await DelayAnimationIntervalsAsync(1, cancellationToken).ConfigureAwait(false);
                    occurredAtUtc = occurredAtUtc.AddSeconds(6);
                    TransferProgressChanged?.Invoke(this, new DesktopTransferProgressSnapshot(
                        DocumentsPairId,
                        SyncTransferDirection.Download,
                        relativePath,
                        currentFileBytes / 2,
                        currentFileBytes,
                        IsCompleted: false,
                        occurredAtUtc,
                        SpeedBytesPerSecond: 8_388_608,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(1)));

                    await DelayAnimationIntervalsAsync(1, cancellationToken).ConfigureAwait(false);
                    occurredAtUtc = occurredAtUtc.AddSeconds(6);
                    TransferProgressChanged?.Invoke(this, new DesktopTransferProgressSnapshot(
                        DocumentsPairId,
                        SyncTransferDirection.Download,
                        relativePath,
                        currentFileBytes,
                        currentFileBytes,
                        IsCompleted: true,
                        occurredAtUtc,
                        SpeedBytesPerSecond: 8_388_608,
                        EstimatedTimeRemaining: TimeSpan.Zero));
                    RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                        DocumentsPairId,
                        SyncRunProgressStage.HydratingCloudFiles,
                        completedAfter,
                        totalFiles,
                        relativePath,
                        startedAtUtc,
                        IsCompleted: false,
                        occurredAtUtc,
                        completedBytesAfter,
                        totalBytes));

                    await DelayAnimationIntervalsAsync(1, cancellationToken).ConfigureAwait(false);
                }

                DateTime completedAtUtc = animationCompletedAtUtc;
                RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                    DocumentsPairId,
                    SyncRunProgressStage.HydratingCloudFiles,
                    totalFiles,
                    totalFiles,
                    string.Empty,
                    startedAtUtc,
                    IsCompleted: true,
                    completedAtUtc,
                    totalBytes,
                    totalBytes));
                await DelayAnimationIntervalsAsync(5, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(DocumentsPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                    new DesktopSyncPairStatusSnapshot(PhotosPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                ]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task AnimateDehydrationProgressAsync(CancellationToken cancellationToken)
        {
            const int displayedFiles = 50;
            const int totalFiles = 1000;
            DateTime animationCompletedAtUtc = DateTime.UtcNow;
            DateTime startedAtUtc = animationCompletedAtUtc.AddSeconds(-(displayedFiles * 10));

            try
            {
                await DelayAnimationIntervalsAsync(30, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < displayedFiles; index++)
                {
                    int completedBefore = index * totalFiles / displayedFiles;
                    int completedAfter = (index + 1) * totalFiles / displayedFiles;
                    string relativePath = "Music/Albums/Album "
                        + (index + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture)
                        + "/track-"
                        + completedAfter.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)
                        + ".flac";
                    DateTime occurredAtUtc = startedAtUtc.AddSeconds(index * 10);

                    RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                        DocumentsPairId,
                        SyncRunProgressStage.DehydratingCloudFiles,
                        completedBefore,
                        totalFiles,
                        relativePath,
                        startedAtUtc,
                        IsCompleted: false,
                        occurredAtUtc));

                    await DelayAnimationIntervalsAsync(2, cancellationToken).ConfigureAwait(false);
                    occurredAtUtc = occurredAtUtc.AddSeconds(8);
                    RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                        DocumentsPairId,
                        SyncRunProgressStage.DehydratingCloudFiles,
                        completedAfter,
                        totalFiles,
                        relativePath,
                        startedAtUtc,
                        IsCompleted: false,
                        occurredAtUtc));

                    await DelayAnimationIntervalsAsync(1, cancellationToken).ConfigureAwait(false);
                }

                DateTime completedAtUtc = animationCompletedAtUtc;
                RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                    DocumentsPairId,
                    SyncRunProgressStage.DehydratingCloudFiles,
                    totalFiles,
                    totalFiles,
                    string.Empty,
                    startedAtUtc,
                    IsCompleted: true,
                    completedAtUtc));
                await DelayAnimationIntervalsAsync(5, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(DocumentsPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                    new DesktopSyncPairStatusSnapshot(PhotosPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                ]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private Task DelayAnimationIntervalsAsync(int count, CancellationToken cancellationToken)
        {
            TimeSpan delay = TimeSpan.FromTicks(_progressAnimationInterval.Ticks * count);
            return Task.Delay(delay, cancellationToken);
        }

        private static string CreateLocalPath(params string[] segments)
        {
            string root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Cotton")
                : "/home/qa/Cotton";
            return segments.Aggregate(root, Path.Combine);
        }

        private IReadOnlyList<DesktopSyncPairSnapshot> CreatePairs(DateTime syncedAt)
        {
            return _scenario is DesktopVisualSmokeScenario.Connecting
                or DesktopVisualSmokeScenario.SignInError
                or DesktopVisualSmokeScenario.AddFolder
                or DesktopVisualSmokeScenario.AddFolderManyRemoteFolders
                or DesktopVisualSmokeScenario.EmptyDashboard
                ? []
                : CreateDashboardPairs(syncedAt);
        }

        private IReadOnlyList<DesktopSyncPairSnapshot> CreateDashboardPairs(DateTime syncedAt)
        {
            return
            [
                new DesktopSyncPairSnapshot(
                    DocumentsPairId,
                    "Documents",
                    CreateLocalPath("Documents"),
                    "/Documents",
                    CreateDocumentsStatus(),
                    Guid.Parse("29f81b10-b9a8-4f1d-88b0-9bdc6861b4e6"),
                    syncedAt,
                    1842,
                    _scenario == DesktopVisualSmokeScenario.Error
                        ? DesktopActionRequiredMessageResolver.MissingDesktopSyncChangesApiMessage
                        : null),
                new DesktopSyncPairSnapshot(
                    PhotosPairId,
                    "Camera uploads",
                    CreateLocalPath("Pictures", "Camera Uploads"),
                    "/Photos/Camera Uploads",
                    CreateCameraUploadsStatus(),
                    Guid.Parse("c88c7b48-66a3-49dc-aee3-dd7b28614f96"),
                    syncedAt.AddMinutes(-3),
                    1859),
            ];
        }

        private string CreateDocumentsStatus()
        {
            return _scenario switch
            {
                DesktopVisualSmokeScenario.Error => "Error",
                DesktopVisualSmokeScenario.Progress => "Syncing",
                DesktopVisualSmokeScenario.ManySmallDownload => "Syncing",
                DesktopVisualSmokeScenario.HydrationProgress => "Syncing",
                DesktopVisualSmokeScenario.DehydrationProgress => "Syncing",
                DesktopVisualSmokeScenario.HighPressureStarting => "Syncing",
                DesktopVisualSmokeScenario.VirtualFilesSeeding => "Syncing",
                _ => "Idle",
            };
        }

        private string CreateCameraUploadsStatus()
        {
            return _scenario == DesktopVisualSmokeScenario.Progress
                ? "Syncing"
                : "Idle";
        }
    }
}
