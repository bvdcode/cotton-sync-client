// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.ViewModels
{
    internal readonly record struct RunFileProgressSample(
        double CompletedFiles,
        int TotalFiles,
        DateTime OccurredAtUtc);
}
