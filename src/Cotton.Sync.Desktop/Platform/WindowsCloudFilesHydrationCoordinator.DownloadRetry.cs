// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesHydrationCoordinator
    {
        private async Task DownloadWithRetryAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            FileStream destination,
            Func<Task> download,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.SetLength(0);
                destination.Position = 0;
                try
                {
                    await download().ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested
                    && attempt < _downloadRetryOptions.MaxAttempts
                    && SyncFailureClassifier.IsTransientConnectionFailure(exception))
                {
                    double delayMilliseconds = Math.Min(
                        _downloadRetryOptions.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
                        _downloadRetryOptions.MaxDelay.TotalMilliseconds);
                    _diagnostics.Record(
                        "hydrate-download", "retrying", identity.SyncPairId.ToString(), null, identity.RelativePath,
                        $"Attempt {attempt} failed; retrying {attempt + 1} of {_downloadRetryOptions.MaxAttempts} "
                            + $"after {delayMilliseconds} ms: {exception.GetType().Name}: {exception.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static void CompleteTransferProgress(IProgress<SyncTransferProgress>? transferProgress)
        {
            if (transferProgress is WindowsCloudFilesAppTransferProgressReporter appProgress)
            {
                appProgress.Complete();
            }
        }
    }
}
