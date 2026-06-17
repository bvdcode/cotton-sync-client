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
        IRemoteFilePlaceholderWriter,
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

        public Task BeforeCreateDirectoryAsync(
            RemoteDirectoryMaterializationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            SuppressProviderWrite(request.SyncPairId, request.LocalRootPath, request.RelativePath);
            return Task.CompletedTask;
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
    }
}
