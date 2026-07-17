// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class LiveSyncSmokeSeedPlanTests
    {
        [Test]
        public void Build_CreatesUniqueAlternatingBurstWithOneZeroByteFile()
        {
            DateTime createdAtUtc = new(2026, 7, 16, 23, 0, 0, DateTimeKind.Utc);

            IReadOnlyList<LiveSyncSmokeSeedFile> files = LiveSyncSmokeSeedPlan.Build(64, createdAtUtc);

            Assert.Multiple(() =>
            {
                Assert.That(files, Has.Count.EqualTo(64));
                Assert.That(files.Select(static file => file.RelativePath), Is.Unique);
                Assert.That(files.Count(static file => file.Content.Length == 0), Is.EqualTo(1));
                Assert.That(files[0].UseFirstClient, Is.True);
                Assert.That(files[1].UseFirstClient, Is.False);
                Assert.That(files[0].RelativePath, Is.EqualTo("pre-existing/burst/client-a/file-00000.bin"));
                Assert.That(files[1].RelativePath, Is.EqualTo("pre-existing/burst/client-b/file-00001.bin"));
                Assert.That(files.Skip(1).Select(static file => file.Content), Is.All.Contains("2026-07-16T23:00:00.0000000Z"));
            });
        }

        [Test]
        public void Build_RejectsNonPositiveCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LiveSyncSmokeSeedPlan.Build(0, DateTime.UtcNow));
        }
    }
}
