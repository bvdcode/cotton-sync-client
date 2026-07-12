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

        [Test]
        public void ForLocalChangedPaths_IncludesDeletedPathsInScope()
        {
            SyncRunScope scope = SyncRunScope.ForLocalChangedPaths(
                ["Docs/report.txt"],
                ["Docs/deleted.txt"]);

            Assert.Multiple(() =>
            {
                Assert.That(scope.IsFull, Is.False);
                Assert.That(scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt", "Docs/report.txt" }));
                Assert.That(scope.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
            });
        }
    }
}
