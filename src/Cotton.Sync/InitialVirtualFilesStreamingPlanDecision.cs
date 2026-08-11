// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal record InitialVirtualFilesStreamingPlanDecision(InitialVirtualFilesStreamingPlan? Plan)
    {
        public static InitialVirtualFilesStreamingPlanDecision NotApplicable { get; } = new(Plan: null);

        public static InitialVirtualFilesStreamingPlanDecision FromPlan(InitialVirtualFilesStreamingPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return new InitialVirtualFilesStreamingPlanDecision(plan);
        }
    }
}
