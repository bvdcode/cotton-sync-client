// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class DesktopCloudFilesPlaceholderWriter :
        IRemoteFilePlaceholderBatchWriter,
        IRemoteFilePlaceholderPopulationObserver,
        IRemoteDirectoryMaterializationObserver
    {
        private readonly Func<SyncPairModeCapabilitySnapshot> _getCapabilities;
        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly IWindowsCloudFilesAdapter _cloudFilesAdapter;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly ILogger<DesktopCloudFilesPlaceholderWriter> _logger;

        public DesktopCloudFilesPlaceholderWriter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            Func<SyncPairModeCapabilitySnapshot>? getCapabilities = null,
            ILocalChangeSuppression? localChangeSuppression = null,
            ILogger<DesktopCloudFilesPlaceholderWriter>? logger = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _cloudFilesAdapter = cloudFilesAdapter ?? new WindowsCloudFilesAdapter(_rootSafety);
            _getCapabilities = getCapabilities ?? DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities;
            _localChangeSuppression = localChangeSuppression;
            _logger = logger ?? NullLogger<DesktopCloudFilesPlaceholderWriter>.Instance;
        }

        public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
            RemoteFilePlaceholderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            SyncPairModeCapabilitySnapshot capabilities = _getCapabilities();
            if (!capabilities.IsWindowsVirtualFilesSupported)
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    request.RelativePath,
                    capabilities.GetUnsupportedMessage(SyncPairMode.WindowsVirtualFiles));
            }

            WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(request.LocalRootPath);
            if (!safety.IsSafe)
            {
                throw new RemoteFilePlaceholderUnavailableException(request.RelativePath, safety.Details);
            }

            try
            {
                SuppressProviderWrite(request.SyncPairId, safety.FullPath, request.RelativePath);
                return Task.FromResult(_cloudFilesAdapter.CreateFilePlaceholder(request));
            }
            catch (Exception exception) when (IsRecoverablePlaceholderFailure(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Windows Cloud Files placeholder creation failed for {RelativePath}.",
                    request.RelativePath);
                throw new RemoteFilePlaceholderUnavailableException(request.RelativePath, exception.Message);
            }
        }

        public Task<IReadOnlyList<RemoteFilePlaceholderBatchResult>> CreatePlaceholdersAsync(
            IReadOnlyList<RemoteFilePlaceholderRequest> requests,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requests);
            cancellationToken.ThrowIfCancellationRequested();
            if (requests.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>([]);
            }

            SyncPairModeCapabilitySnapshot capabilities = _getCapabilities();
            if (!capabilities.IsWindowsVirtualFilesSupported)
            {
                return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>(
                    requests
                        .Select(request => RemoteFilePlaceholderBatchResult.Unavailable(
                            request,
                            capabilities.GetUnsupportedMessage(SyncPairMode.WindowsVirtualFiles)))
                        .ToArray());
            }

            foreach (RemoteFilePlaceholderRequest request in requests)
            {
                WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(request.LocalRootPath);
                if (!safety.IsSafe)
                {
                    return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>(
                        requests
                            .Select(item => RemoteFilePlaceholderBatchResult.Unavailable(item, safety.Details))
                            .ToArray());
                }

                SuppressProviderWrite(request.SyncPairId, safety.FullPath, request.RelativePath);
            }

            try
            {
                IReadOnlyList<RemoteFilePlaceholderResult> placeholders = _cloudFilesAdapter.CreateFilePlaceholders(requests);
                if (placeholders.Count != requests.Count)
                {
                    throw new InvalidOperationException("Cloud Files adapter returned a different number of placeholder results.");
                }

                var results = new RemoteFilePlaceholderBatchResult[requests.Count];
                for (int index = 0; index < requests.Count; index++)
                {
                    results[index] = RemoteFilePlaceholderBatchResult.Success(requests[index], placeholders[index]);
                }

                return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>(results);
            }
            catch (Exception exception) when (IsRecoverablePlaceholderFailure(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Windows Cloud Files batch placeholder creation failed for {PlaceholderCount} placeholders.",
                    requests.Count);
                return CreatePlaceholdersIndividuallyAfterBatchFailureAsync(requests, cancellationToken);
            }
        }

        public IDisposable BeginPopulation(string syncPairIdValue, string localRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairIdValue);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            if (_localChangeSuppression is null)
            {
                return NoopDisposable.Instance;
            }

            if (!Guid.TryParse(syncPairIdValue, out Guid syncPairId))
            {
                _logger.LogDebug(
                    "Skipping local watcher burst suppression because sync pair id is not a GUID.");
                return NoopDisposable.Instance;
            }

            return _localChangeSuppression.SuppressProviderWriteBurst(syncPairId, localRootPath);
        }

        public Task BeforeCreateDirectoryAsync(
            RemoteDirectoryMaterializationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            SuppressProviderWrite(request.SyncPairId, request.LocalRootPath, request.RelativePath);
            return Task.CompletedTask;
        }

        public Task AfterCreateDirectoryAsync(
            RemoteDirectoryMaterializationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(request.SyncPairId, out Guid syncPairId))
            {
                _logger.LogDebug(
                    "Skipping Cloud Files in-sync finalization for provider-created directory {RelativePath} because sync pair id is not a GUID.",
                    request.RelativePath);
                return Task.CompletedTask;
            }

            try
            {
                _cloudFilesAdapter.SetInSyncState(
                    new SyncPairSettings
                    {
                        Id = syncPairId,
                        DisplayName = "Cotton Sync",
                        LocalRootPath = request.LocalRootPath,
                        RemoteDisplayPath = "/",
                        RemoteRootNodeId = request.RemoteRootNodeId,
                        Mode = SyncPairMode.WindowsVirtualFiles,
                        IsEnabled = true,
                    },
                    request.RelativePath);
            }
            catch (Exception exception) when (IsRecoverablePlaceholderFailure(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Windows Cloud Files in-sync finalization failed for directory {RelativePath}.",
                    request.RelativePath);
                throw;
            }

            return Task.CompletedTask;
        }

        private async Task<IReadOnlyList<RemoteFilePlaceholderBatchResult>> CreatePlaceholdersIndividuallyAfterBatchFailureAsync(
            IReadOnlyList<RemoteFilePlaceholderRequest> requests,
            CancellationToken cancellationToken)
        {
            var results = new RemoteFilePlaceholderBatchResult[requests.Count];
            for (int index = 0; index < requests.Count; index++)
            {
                RemoteFilePlaceholderRequest request = requests[index];
                try
                {
                    RemoteFilePlaceholderResult placeholder =
                        await CreatePlaceholderAsync(request, cancellationToken).ConfigureAwait(false);
                    results[index] = RemoteFilePlaceholderBatchResult.Success(request, placeholder);
                }
                catch (RemoteFilePlaceholderUnavailableException exception)
                {
                    results[index] = RemoteFilePlaceholderBatchResult.Unavailable(request, exception.Reason);
                }
            }

            return results;
        }

        private static bool IsRecoverablePlaceholderFailure(Exception exception)
        {
            return exception is WindowsCloudFilesNativeException
                or InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException;
        }

        private void SuppressProviderWrite(string syncPairIdValue, string localRootPath, string relativePath)
        {
            if (_localChangeSuppression is null)
            {
                return;
            }

            if (!Guid.TryParse(syncPairIdValue, out Guid syncPairId))
            {
                _logger.LogDebug(
                    "Skipping local watcher suppression for provider write {RelativePath} because sync pair id is not a GUID.",
                    relativePath);
                return;
            }

            _localChangeSuppression.SuppressProviderWrite(syncPairId, localRootPath, relativePath);
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
