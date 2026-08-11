// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync
{
    internal record ScopedVirtualFilesDirectoryRenameValidation(
        IReadOnlyDictionary<string, string> TargetPathBySourceKey,
        IReadOnlySet<string> ExpectedSourceDirectoryKeys,
        IReadOnlySet<string> ExpectedSourceFileKeys,
        LocalTreeLookupSnapshot LocalDescendants,
        RemoteTreeLookupSnapshot RemoteDescendants);
}
