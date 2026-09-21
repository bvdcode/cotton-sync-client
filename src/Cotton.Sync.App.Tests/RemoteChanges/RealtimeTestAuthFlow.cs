// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    internal class RealtimeTestAuthFlow : IAuthFlow
    {
        public int RestoreCalls { get; private set; }

        public Func<CancellationToken, Task>? RestoreAction { get; set; }

        public async Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            if (RestoreAction is not null)
            {
                await RestoreAction(cancellationToken);
            }

            return new AuthSession(Guid.NewGuid(), "test-user", null, false);
        }

        public Task<AuthSession> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
