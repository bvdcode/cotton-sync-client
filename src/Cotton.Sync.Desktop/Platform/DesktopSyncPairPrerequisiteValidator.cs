// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal class DesktopSyncPairPrerequisiteValidator : ISyncPairPrerequisiteValidator
    {
        private readonly ISyncPairPrerequisiteValidator _inner;
        private readonly WindowsVirtualFilesRootSafetyPolicy _rootSafety;

        public DesktopSyncPairPrerequisiteValidator(
            ISyncPairPrerequisiteValidator inner,
            WindowsVirtualFilesRootSafetyPolicy? rootSafety = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _rootSafety = rootSafety ?? new WindowsVirtualFilesRootSafetyPolicy();
        }

        public Task<IReadOnlyList<SyncPairValidationError>> ValidateAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            cancellationToken.ThrowIfCancellationRequested();
            if (syncPair.Mode == SyncPairMode.WindowsVirtualFiles)
            {
                WindowsVirtualFilesRootSafetyResult safety = _rootSafety.Validate(syncPair.LocalRootPath);
                if (!safety.IsSafe)
                {
                    IReadOnlyList<SyncPairValidationError> errors =
                    [
                        new SyncPairValidationError(
                            SyncPairValidationIssue.LocalRootUnavailable,
                            syncPair.Id,
                            null,
                            safety.Details),
                    ];
                    return Task.FromResult(errors);
                }
            }

            return _inner.ValidateAsync(syncPair, cancellationToken);
        }
    }
}
