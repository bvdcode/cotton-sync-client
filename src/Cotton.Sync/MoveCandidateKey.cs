// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal readonly record struct MoveCandidateKey(string ContentHash, long SizeBytes);
}
