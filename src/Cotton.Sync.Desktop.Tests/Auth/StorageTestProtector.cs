// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    internal class StorageTestProtector(string scheme) : ITokenPayloadProtector
    {
        public string Scheme => scheme;
        public int Reads { get; private set; }

        public Task<byte[]> ProtectAsync(byte[] plaintext, CancellationToken cancellationToken = default)
            => Task.FromResult(plaintext.ToArray());

        public Task<byte[]> UnprotectAsync(byte[] payload, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(payload.ToArray());
        }
    }
}
