// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal class AlwaysKeepPopulationWatcherCoordinator(
        LocalChangeSuppression suppression,
        SyncPairRunner runner,
        string folderPath,
        string relativeFolderPath,
        Task continuePopulation)
    {
        private readonly LocalChangeSuppression _suppression =
            suppression ?? throw new ArgumentNullException(nameof(suppression));
        private readonly SyncPairRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        private readonly string _folderPath = Path.GetFullPath(folderPath);
        private readonly string _relativeFolderPath = relativeFolderPath;
        private readonly Task _continuePopulation =
            continuePopulation ?? throw new ArgumentNullException(nameof(continuePopulation));
        private readonly TaskCompletionSource<bool> _requestQueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queueStarted;

        public bool QueuedDuringPopulation { get; private set; }

        public Task RequestQueued => _requestQueued.Task;

        public void OnWatcherChanged(object? sender, LocalSyncRootChange change)
        {
            if (_suppression.ShouldSuppress(change) || !TargetsFolder(change.FullPath))
            {
                return;
            }

            if (!TryReadPinnedFolder() || Interlocked.CompareExchange(ref _queueStarted, 1, 0) != 0)
            {
                return;
            }

            _ = QueueAvailabilityAsync();
        }

        private bool TargetsFolder(string fullPath)
        {
            return string.Equals(Path.GetFullPath(fullPath), _folderPath, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryReadPinnedFolder()
        {
            try
            {
                return DesktopWindowsVirtualFilesSmokeRunner.HasPinned(File.GetAttributes(_folderPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private async Task QueueAvailabilityAsync()
        {
            try
            {
                QueuedDuringPopulation = !_continuePopulation.IsCompleted
                    && _runner.Status.State == SyncPairRunState.Syncing;
                await _runner
                    .SyncNowAsync(
                        SyncRunRequest.ForLocalChangedPaths([_relativeFolderPath]),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _requestQueued.TrySetResult(true);
            }
            catch (Exception exception)
            {
                _requestQueued.TrySetException(exception);
            }
        }
    }
}
