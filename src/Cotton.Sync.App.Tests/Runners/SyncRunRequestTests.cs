// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.Runners
{
    public class SyncRunRequestTests
    {
        [Test]
        public void ForLocalChangedPaths_RejectsEmptyScope()
        {
            Assert.Throws<ArgumentException>(() => SyncRunRequest.ForLocalChangedPaths(Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => SyncRunRequest.ForLocalChangedPaths(["", " "]));
        }
    }
}
