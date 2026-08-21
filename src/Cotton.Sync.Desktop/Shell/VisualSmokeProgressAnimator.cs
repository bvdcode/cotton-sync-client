// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal class VisualSmokeProgressAnimator(
        DesktopVisualSmokeScenario scenario,
        TimeSpan progressAnimationInterval) : IDisposable, IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private Task? _animationTask;
        private int _animationStarted;
        private int _isDisposed;

        public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged;

        public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged;

        public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged;

        public void Start()
        {
            if ((scenario is not DesktopVisualSmokeScenario.HydrationProgress
                    and not DesktopVisualSmokeScenario.DehydrationProgress)
                || Interlocked.Exchange(ref _animationStarted, 1) != 0)
            {
                return;
            }

            _animationTask = scenario == DesktopVisualSmokeScenario.HydrationProgress
                ? AnimateHydrationProgressAsync(_lifetimeCancellation.Token)
                : AnimateDehydrationProgressAsync(_lifetimeCancellation.Token);
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
                if (_animationTask is not null)
                {
                    await _animationTask.ConfigureAwait(false);
                }
            }
            finally
            {
                _lifetimeCancellation.Dispose();
            }
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
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                        VisualSmokeFixtureIds.DocumentsPairId,
                        SyncTransferDirection.Download,
                        relativePath,
                        currentFileBytes,
                        currentFileBytes,
                        IsCompleted: true,
                        occurredAtUtc,
                        SpeedBytesPerSecond: 8_388_608,
                        EstimatedTimeRemaining: TimeSpan.Zero));
                    RunProgressChanged?.Invoke(this, new DesktopRunProgressSnapshot(
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                    VisualSmokeFixtureIds.DocumentsPairId,
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
                    new DesktopSyncPairStatusSnapshot(VisualSmokeFixtureIds.DocumentsPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                    new DesktopSyncPairStatusSnapshot(VisualSmokeFixtureIds.PhotosPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
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
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                        VisualSmokeFixtureIds.DocumentsPairId,
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
                    VisualSmokeFixtureIds.DocumentsPairId,
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
                    new DesktopSyncPairStatusSnapshot(VisualSmokeFixtureIds.DocumentsPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                    new DesktopSyncPairStatusSnapshot(VisualSmokeFixtureIds.PhotosPairId, "Idle", null, LastSyncedAtUtc: completedAtUtc),
                ]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private Task DelayAnimationIntervalsAsync(int count, CancellationToken cancellationToken)
        {
            TimeSpan delay = TimeSpan.FromTicks(progressAnimationInterval.Ticks * count);
            return Task.Delay(delay, cancellationToken);
        }
    }
}
