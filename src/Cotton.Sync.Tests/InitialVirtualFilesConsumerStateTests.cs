// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Tests
{
    public class InitialVirtualFilesConsumerStateTests
    {
        [Test]
        public void Constructor_DoesNotAllocateMissingFileIndexForFreshPopulation()
        {
            InitialVirtualFilesConsumerState state = new(
                stateBatchSize: 512,
                placeholderBatchSize: 64,
                placeholderConcurrency: 4,
                trackDirectoryFinalization: true,
                trackMissingRemoteFiles: false,
                StringComparer.OrdinalIgnoreCase);

            Assert.That(state.StreamedRemoteFilePaths, Is.Null);
        }

        [Test]
        public void Constructor_AllocatesMissingFileIndexForResumePopulation()
        {
            InitialVirtualFilesConsumerState state = new(
                stateBatchSize: 512,
                placeholderBatchSize: 64,
                placeholderConcurrency: 4,
                trackDirectoryFinalization: true,
                trackMissingRemoteFiles: true,
                StringComparer.OrdinalIgnoreCase);

            Assert.That(state.StreamedRemoteFilePaths, Is.Not.Null);
        }
    }
}
