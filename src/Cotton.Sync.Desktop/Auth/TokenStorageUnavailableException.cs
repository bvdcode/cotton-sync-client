// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Auth
{
    internal class TokenStorageUnavailableException(string message) : CryptographicException(message)
    {
    }
}
