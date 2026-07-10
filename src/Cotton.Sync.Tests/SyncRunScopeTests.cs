// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Tests
{
    public class SyncRunScopeTests
    {
        [Test]
        public void ForLocalChangedPaths_RejectsEmptyScope()
        {
            Assert.Throws<ArgumentException>(() => SyncRunScope.ForLocalChangedPaths(Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => SyncRunScope.ForLocalChangedPaths(["", " "]));
        }
    }
}
