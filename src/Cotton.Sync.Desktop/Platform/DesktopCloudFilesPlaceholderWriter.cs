// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class DesktopCloudFilesPlaceholderWriter : IRemoteFilePlaceholderWriter
    {
        private const string NativePlaceholderCreationPendingMessage =
            "Windows Cloud Files placeholder creation is not connected yet. Keep this sync pair in full-mirror mode until native placeholder creation is implemented.";

        private readonly Func<SyncPairModeCapabilitySnapshot> _getCapabilities;
        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;
        private readonly ILogger<DesktopCloudFilesPlaceholderWriter> _logger;

        public DesktopCloudFilesPlaceholderWriter(
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null,
            Func<SyncPairModeCapabilitySnapshot>? getCapabilities = null,
            ILogger<DesktopCloudFilesPlaceholderWriter>? logger = null)
        {
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
            _getCapabilities = getCapabilities ?? DesktopCloudFilesCapabilities.CreateSyncPairModeCapabilities;
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

            _logger.LogWarning(
                "Windows virtual-files placeholder creation is not implemented for {RelativePath}.",
                request.RelativePath);
            throw new RemoteFilePlaceholderUnavailableException(
                request.RelativePath,
                NativePlaceholderCreationPendingMessage);
        }
    }
}
