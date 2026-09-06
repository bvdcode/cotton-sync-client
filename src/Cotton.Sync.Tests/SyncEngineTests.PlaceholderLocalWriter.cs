// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        private class PlaceholderLocalWriter(LocalFileSnapshot placeholder) : ILocalFileSyncWriter
        {
            private readonly AtomicLocalFileSyncWriter _writer = new();

            public int DeleteCalls { get; private set; }

            public Task<LocalFileWriteResult> WriteFileAsync(string rootPath, string relativePath,
                Func<Stream, CancellationToken, Task> writeContentAsync, DateTime? lastWriteUtc = null,
                string? expectedLocalContentHash = null, CancellationToken cancellationToken = default)
            {
                return _writer.WriteFileAsync(rootPath, relativePath, writeContentAsync, lastWriteUtc, expectedLocalContentHash, cancellationToken);
            }

            public async Task DeleteFileAsync(string rootPath, string relativePath, LocalFileSnapshot? expectedLocalFile,
                CancellationToken cancellationToken = default)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(expectedLocalFile, Is.SameAs(placeholder));
                    Assert.That(expectedLocalFile!.IsCloudFilesPlaceholder, Is.True);
                    Assert.That(expectedLocalFile.IsCloudFilesOnlineOnlyPlaceholder, Is.EqualTo(placeholder.IsCloudFilesOnlineOnlyPlaceholder));
                    Assert.That(relativePath, Is.EqualTo(placeholder.RelativePath));
                });
                DeleteCalls++;
                IReadOnlyList<LocalFileSnapshot> files = await new LocalFileScanner().ScanAsync(rootPath, cancellationToken);
                LocalFileSnapshot physicalFile = files.Single(file => file.RelativePath == relativePath);
                await _writer.DeleteFileAsync(rootPath, relativePath, physicalFile, cancellationToken);
            }

            public Task CreateDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default)
            {
                return _writer.CreateDirectoryAsync(rootPath, relativePath, cancellationToken);
            }

            public Task MoveDirectoryAsync(string rootPath, string sourceRelativePath, string targetRelativePath,
                CancellationToken cancellationToken = default)
            {
                return _writer.MoveDirectoryAsync(rootPath, sourceRelativePath, targetRelativePath, cancellationToken);
            }

            public Task DeleteDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default)
            {
                return _writer.DeleteDirectoryAsync(rootPath, relativePath, cancellationToken);
            }

            public string CreateConflictRelativePath(string rootPath, string relativePath, DateTime timestampUtc)
            {
                return _writer.CreateConflictRelativePath(rootPath, relativePath, timestampUtc);
            }
        }
    }
}
