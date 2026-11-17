// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Sdk.Auth;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    internal class CancellingRefreshTokenStore(ICottonTokenStore inner, CancellationTokenSource cancellation) : ICottonTokenStore
    {
        public Task<TokenPairDto?> GetAsync(CancellationToken cancellationToken = default) => inner.GetAsync(cancellationToken);

        public async Task SaveAsync(TokenPairDto tokens, CancellationToken cancellationToken = default)
        {
            await cancellation.CancelAsync();
            await inner.SaveAsync(tokens, cancellationToken);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);
    }
}
