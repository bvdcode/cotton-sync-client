// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class LiveSyncSmokeStateExpectationTests
    {
        [Test]
        public void BuildRelativePaths_EmptyFileListReturnsNoPaths()
        {
            IReadOnlyList<string> paths = LiveSyncSmokeStateExpectation.BuildRelativePaths([]);

            Assert.That(paths, Is.Empty);
        }

        [Test]
        public void BuildRelativePaths_IncludesSharedAncestorDirectories()
        {
            IReadOnlyList<string> paths = LiveSyncSmokeStateExpectation.BuildRelativePaths(
                [
                    @"pre-existing\client-a\original-a.txt",
                    "pre-existing/client-b/original-b.txt",
                ]);

            Assert.That(
                paths,
                Is.EquivalentTo(
                    new[]
                    {
                        "pre-existing",
                        "pre-existing/client-a",
                        "pre-existing/client-a/original-a.txt",
                        "pre-existing/client-b",
                        "pre-existing/client-b/original-b.txt",
                    }));
        }

        [Test]
        public void BuildRelativePaths_DeduplicatesEquivalentFilePaths()
        {
            IReadOnlyList<string> paths = LiveSyncSmokeStateExpectation.BuildRelativePaths(
                [
                    @"folder\file.txt",
                    "FOLDER/file.txt",
                ]);

            Assert.That(paths, Has.Count.EqualTo(2));
            Assert.That(paths, Does.Contain("folder"));
            Assert.That(paths, Does.Contain("folder/file.txt"));
        }
    }
}
