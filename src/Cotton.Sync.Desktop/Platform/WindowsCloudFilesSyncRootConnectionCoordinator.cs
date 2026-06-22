// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesSyncRootConnectionCoordinator : ISyncCoreLifecycleComponent
    {
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly IWindowsCloudFilesCallbackHandler _callbackHandler;
        private readonly ILogger<WindowsCloudFilesSyncRootConnectionCoordinator> _logger;
        private readonly ISyncPairSettingsStore _syncPairs;
        private readonly Dictionary<Guid, WindowsCloudFilesConnection> _connections = [];

        public WindowsCloudFilesSyncRootConnectionCoordinator(
            ISyncPairSettingsStore syncPairs,
            IWindowsCloudFilesAdapter cloudFiles,
            IWindowsCloudFilesCallbackHandler callbackHandler,
            ILogger<WindowsCloudFilesSyncRootConnectionCoordinator>? logger = null)
        {
            _syncPairs = syncPairs ?? throw new ArgumentNullException(nameof(syncPairs));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _callbackHandler = callbackHandler ?? throw new ArgumentNullException(nameof(callbackHandler));
            _logger = logger ?? NullLogger<WindowsCloudFilesSyncRootConnectionCoordinator>.Instance;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken, throwOnFailure: true).ConfigureAwait(false);
                try
                {
                    await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairs
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    foreach (SyncPairSettings syncPair in syncPairs.Where(RequiresCloudFilesConnection))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WindowsCloudFilesConnection connection =
                            _cloudFiles.ConnectSyncRoot(syncPair, _callbackHandler);
                        _connections[syncPair.Id] = connection;
                        _logger.LogInformation(
                            "Connected Windows Cloud Files sync root for {SyncPairId} at {LocalRootPath}.",
                            syncPair.Id,
                            connection.LocalRootPath);
                    }
                }
                catch
                {
                    await StopCoreAsync(CancellationToken.None, throwOnFailure: false).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken, throwOnFailure: true).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private static bool RequiresCloudFilesConnection(SyncPairSettings syncPair)
        {
            return syncPair.IsEnabled && syncPair.Mode == SyncPairMode.WindowsVirtualFiles;
        }

        private Task StopCoreAsync(CancellationToken cancellationToken, bool throwOnFailure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_connections.Count == 0)
            {
                return Task.CompletedTask;
            }

            List<Exception>? failures = null;
            foreach ((Guid syncPairId, WindowsCloudFilesConnection connection) in _connections.ToArray().Reverse())
            {
                try
                {
                    connection.Dispose();
                    _logger.LogInformation(
                        "Disconnected Windows Cloud Files sync root for {SyncPairId} at {LocalRootPath}.",
                        syncPairId,
                        connection.LocalRootPath);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Failed to disconnect Windows Cloud Files sync root for {SyncPairId} at {LocalRootPath}.",
                        syncPairId,
                        connection.LocalRootPath);
                    (failures ??= []).Add(exception);
                }
            }

            _connections.Clear();
            if (throwOnFailure && failures is { Count: > 0 })
            {
                throw new AggregateException(
                    "One or more Windows Cloud Files sync roots failed to disconnect.",
                    failures);
            }

            return Task.CompletedTask;
        }
    }
}
