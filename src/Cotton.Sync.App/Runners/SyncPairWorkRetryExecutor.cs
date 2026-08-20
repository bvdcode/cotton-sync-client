// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Runners
{
    internal class SyncPairWorkRetryExecutor
    {
        private readonly ILogger _logger;
        private readonly SyncPairRunnerRetryOptions _options;
        private readonly SyncPairSettings _syncPair;
        private readonly Action<SyncPairRunState, string?> _setState;
        private readonly ISyncPairWork _work;

        public SyncPairWorkRetryExecutor(
            SyncPairSettings syncPair,
            ISyncPairWork work,
            SyncPairRunnerRetryOptions options,
            Action<SyncPairRunState, string?> setState,
            ILogger logger)
        {
            _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
            _work = work ?? throw new ArgumentNullException(nameof(work));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _setState = setState ?? throw new ArgumentNullException(nameof(setState));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RunAsync(SyncRunRequest request, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await _work.RunOnceAsync(_syncPair, request, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (LocalFileUnavailableException exception) when (
                    attempt >= _options.MaxAttempts
                    && ShouldWaitForLocalFileAvailability(exception))
                {
                    await WaitForLocalFileAvailabilityAsync(exception, attempt, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRetriableSyncFailure(exception) && attempt < _options.MaxAttempts)
                {
                    TimeSpan delay = GetRetryDelay(attempt);
                    _setState(GetRetriableFailureState(exception), CreateFailureMessage(exception));
                    _logger.LogWarning(
                        exception,
                        "Retriable sync failure for {SyncPairId}; retrying attempt {NextAttempt} of {MaxAttempts} after {Delay}.",
                        _syncPair.Id,
                        attempt + 1,
                        _options.MaxAttempts,
                        delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    _setState(SyncPairRunState.Syncing, null);
                }
            }
        }

        public static string CreateFailureMessage(Exception exception)
        {
            return exception switch
            {
                RemoteChangeFeedUnavailableException unavailableException => unavailableException.Message,
                CottonApiException apiException when apiException.StatusCode == HttpStatusCode.Locked
                    => "Cotton Cloud reports that the server is locked. Cotton Sync will retry automatically.",
                _ when SyncFailureClassifier.IsTransientConnectionFailure(exception)
                    => "Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically.",
                CottonApiException apiException when apiException.StatusCode == HttpStatusCode.Unauthorized
                    => "Session expired. Sign in again to continue syncing.",
                CottonApiException apiException when apiException.StatusCode == HttpStatusCode.Forbidden
                    => "Cotton Cloud denied access to this sync folder. Check account permissions and sign in again if needed.",
                CottonApiException apiException when apiException.StatusCode == HttpStatusCode.Conflict
                    => "Cotton Cloud reported a conflict while syncing. Review conflicts and retry.",
                CottonApiException apiException when IsQuotaExceededStatus(apiException.StatusCode)
                    => "Remote storage quota exceeded. Free space in Cotton Cloud or choose a smaller sync folder.",
                CottonApiException apiException when apiException.StatusCode == HttpStatusCode.RequestEntityTooLarge
                    => "Remote upload was rejected because it is larger than the server limit.",
                UnauthorizedAccessException
                    => "Permission denied while accessing local sync files. Check folder permissions and retry.",
                LocalInsufficientDiskSpaceException
                    => "Local disk is full. Free space on this computer and retry sync.",
                IOException ioException when IsDiskFull(ioException)
                    => "Local disk is full. Free space on this computer and retry sync.",
                DirectoryNotFoundException
                    => "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.",
                LocalFileUnavailableException localFileUnavailable
                    => "Local file is not ready yet: " + localFileUnavailable.RelativePath + ". Sync will retry.",
                _ => exception.Message,
            };
        }

        private async Task WaitForLocalFileAvailabilityAsync(
            LocalFileUnavailableException exception,
            int completedAttempts,
            CancellationToken cancellationToken)
        {
            string message = CreateFailureMessage(exception);
            int availabilityAttempt = completedAttempts;
            bool firstWait = true;
            while (true)
            {
                TimeSpan delay = GetRetryDelay(availabilityAttempt);
                if (delay == TimeSpan.Zero)
                {
                    delay = TimeSpan.FromMilliseconds(10);
                }

                _setState(SyncPairRunState.Waiting, message);
                LogAvailabilityWait(exception, completedAttempts, delay, firstWait);
                firstWait = false;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (IsLocalFileReady(exception))
                {
                    _logger.LogInformation(
                        "Local file {RelativePath} became available; resuming sync.",
                        exception.RelativePath);
                    _setState(SyncPairRunState.Syncing, null);
                    return;
                }

                availabilityAttempt++;
            }
        }

        private void LogAvailabilityWait(
            LocalFileUnavailableException exception,
            int completedAttempts,
            TimeSpan delay,
            bool firstWait)
        {
            if (firstWait)
            {
                _logger.LogWarning(
                    "Local file {RelativePath} remains unavailable after {AttemptCount} attempts; waiting {Delay} before checking it again.",
                    exception.RelativePath,
                    completedAttempts,
                    delay);
                return;
            }

            _logger.LogDebug(
                "Local file {RelativePath} is still unavailable; checking it again after {Delay}.",
                exception.RelativePath,
                delay);
        }

        private TimeSpan GetRetryDelay(int completedAttempts)
        {
            if (_options.InitialDelay == TimeSpan.Zero || _options.MaxDelay == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            double multiplier = Math.Pow(2, Math.Max(0, completedAttempts - 1));
            double milliseconds = Math.Min(
                _options.InitialDelay.TotalMilliseconds * multiplier,
                _options.MaxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private static bool IsLocalFileReady(LocalFileUnavailableException exception)
        {
            try
            {
                FileShare fileShare = exception.RequiresExclusiveAccess
                    ? FileShare.None
                    : FileShare.ReadWrite | FileShare.Delete;
                using FileStream stream = new(
                    exception.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    fileShare,
                    bufferSize: 1,
                    FileOptions.SequentialScan);
                return true;
            }
            catch (Exception readyException) when (readyException is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static bool ShouldWaitForLocalFileAvailability(LocalFileUnavailableException exception)
        {
            return exception.RequiresExclusiveAccess
                || exception.InnerException is IOException innerException && IsSharingViolation(innerException);
        }

        private static bool IsSharingViolation(IOException exception)
        {
            int errorCode = exception.HResult & 0xFFFF;
            return errorCode is 32 or 33;
        }

        private static bool IsQuotaExceededStatus(HttpStatusCode? statusCode)
        {
            return statusCode.HasValue && (int)statusCode.Value == 507;
        }

        private static bool IsDiskFull(IOException exception)
        {
            int errorCode = exception.HResult & 0xFFFF;
            return errorCode is 28 or 39 or 112;
        }

        private static bool IsRetriableSyncFailure(Exception exception)
        {
            return SyncFailureClassifier.IsTransientConnectionFailure(exception)
                || exception is DirectoryNotFoundException
                || exception is LocalFileUnavailableException;
        }

        private static SyncPairRunState GetRetriableFailureState(Exception exception)
        {
            if (exception is LocalFileUnavailableException)
            {
                return SyncPairRunState.Waiting;
            }

            return SyncFailureClassifier.IsTransientConnectionFailure(exception)
                ? SyncPairRunState.Offline
                : SyncPairRunState.Error;
        }
    }
}
