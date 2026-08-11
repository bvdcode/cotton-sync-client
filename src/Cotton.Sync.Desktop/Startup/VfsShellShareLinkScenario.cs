// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.ShellIntegration;

namespace Cotton.Sync.Desktop.Startup
{
    internal record VfsShellShareLinkScenario(
        string Label,
        string SelectedPath,
        string ExpectedRelativePath,
        ShellShareLinkTargetKind ExpectedKind,
        bool ExpectCopied,
        string? ExpectedFailureReason);
}
