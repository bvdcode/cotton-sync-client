// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Runners
{
    internal class SyncPairStatusController
    {
        private readonly object _gate = new();
        private readonly SyncPairSettings _syncPair;
        private DateTime? _lastSuccessfulSyncAtUtc;
        private string? _retainedActionRequiredError;
        private SyncPairStatus _status;

        public SyncPairStatusController(SyncPairSettings syncPair)
        {
            _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
            _status = CreateStatus(syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled);
        }

        public SyncPairStatus Status
        {
            get
            {
                lock (_gate)
                {
                    return _status;
                }
            }
        }

        public void SetState(SyncPairRunState state, string? lastError = null)
        {
            lock (_gate)
            {
                _status = CreateStatus(state, lastError);
            }
        }

        public void SetReadyState()
        {
            if (!_syncPair.IsEnabled)
            {
                SetState(SyncPairRunState.Disabled);
                return;
            }

            string? localRootError = GetLocalRootError(_syncPair.LocalRootPath);
            if (localRootError is not null)
            {
                SetActionRequiredState(localRootError);
                return;
            }

            SetIdleOrActionRequiredState();
        }

        public void SetSuccessfulSyncState(SyncRunRequest request)
        {
            lock (_gate)
            {
                _lastSuccessfulSyncAtUtc = DateTime.UtcNow;
                if (request.ApprovedRemoteDeletePlan is not null
                    || (request.IsFull && (request.Causes & SyncRunCause.Manual) != SyncRunCause.None))
                {
                    _retainedActionRequiredError = null;
                }

                _status = _retainedActionRequiredError is null
                    ? CreateStatus(SyncPairRunState.Idle)
                    : CreateStatus(SyncPairRunState.Error, _retainedActionRequiredError);
            }
        }

        public void SetIdleOrActionRequiredState()
        {
            lock (_gate)
            {
                _status = _retainedActionRequiredError is null
                    ? CreateStatus(SyncPairRunState.Idle)
                    : CreateStatus(SyncPairRunState.Error, _retainedActionRequiredError);
            }
        }

        public void SetActionRequiredState(string message)
        {
            lock (_gate)
            {
                _retainedActionRequiredError = message;
                _status = CreateStatus(SyncPairRunState.Error, message);
            }
        }

        private SyncPairStatus CreateStatus(SyncPairRunState state, string? lastError = null)
        {
            return new SyncPairStatus(
                _syncPair.Id,
                _syncPair.DisplayName,
                state,
                CreateCurrentOperation(state, lastError),
                lastError,
                DateTime.UtcNow,
                _lastSuccessfulSyncAtUtc);
        }

        private static string? CreateCurrentOperation(SyncPairRunState state, string? lastError)
        {
            return state switch
            {
                SyncPairRunState.Scanning => "Scanning changes",
                SyncPairRunState.Syncing => "Syncing changes",
                SyncPairRunState.Waiting => string.IsNullOrWhiteSpace(lastError)
                    ? "Waiting for a local file"
                    : lastError.Trim(),
                SyncPairRunState.Offline => string.IsNullOrWhiteSpace(lastError)
                    ? "Waiting for connection"
                    : "Waiting for connection: " + lastError.Trim(),
                SyncPairRunState.Error => string.IsNullOrWhiteSpace(lastError)
                    ? "Action required"
                    : "Action required: " + lastError.Trim(),
                SyncPairRunState.Conflict => "Conflict needs review",
                _ => null,
            };
        }

        private static string? GetLocalRootError(string localRootPath)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(localRootPath);
                return (attributes & FileAttributes.Directory) != 0
                    ? null
                    : "The configured local sync path is not a folder.";
            }
            catch (Exception exception) when (exception is FileNotFoundException
                or DirectoryNotFoundException
                or DriveNotFoundException)
            {
                return "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.";
            }
            catch (UnauthorizedAccessException)
            {
                return "Permission denied while accessing the local sync folder. Check folder permissions and retry.";
            }
            catch (IOException exception)
            {
                return "Cotton Sync cannot access the local sync folder: " + exception.Message;
            }
        }
    }
}
