// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        public async Task<DesktopTokenStorageCapabilitySnapshot> CheckTokenStorageAsync(CancellationToken cancellationToken = default)
        {
            await _tokenStorage.RetryAsync(cancellationToken).ConfigureAwait(false);
            return await _tokenStorageVerifier(cancellationToken).ConfigureAwait(false);
        }

        public Task ApproveUnencryptedTokenStorageAsync(CancellationToken cancellationToken = default)
        {
            return _tokenStorage.ApproveUnencryptedFileAsync(cancellationToken);
        }
    }
}
