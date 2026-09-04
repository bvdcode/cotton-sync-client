// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Local;

namespace Cotton.Sync.Tests.Local
{
    [Platform("Linux")]
    public class LinuxAtomicFileCommitTests
    {
        private const int ConcurrentCommitIterations = 64;
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-atomic-commit", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task Exchange_PreservesDisplacedFileAndOpenDescriptorAtRecoveryPath()
        {
            string targetPath = Path.Combine(_root, "документ.txt");
            string recoveryPath = Path.Combine(_root, ".cotton-sync", "deleted", "interrupted", "документ.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            await File.WriteAllTextAsync(targetPath, "original");
            await File.WriteAllTextAsync(recoveryPath, "remote");
            await using (FileStream editor = new(targetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                LinuxAtomicFileCommit.Exchange(recoveryPath, targetPath);
                await editor.WriteAsync("late local edit"u8.ToArray());
                await editor.FlushAsync();
            }

            AtomicLocalFileSyncWriter writer = new();
            await writer.WriteFileAsync(_root, "another.txt", WriteRemoteContentAsync);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("remote"));
                Assert.That(File.ReadAllText(recoveryPath), Is.EqualTo("late local edit"));
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });
        }

        [Test]
        public async Task Exchange_MissingDestinationKeepsRecoverySource()
        {
            string sourcePath = Path.Combine(_root, "remote.txt");
            string destinationPath = Path.Combine(_root, "deleted.txt");
            await File.WriteAllTextAsync(sourcePath, "remote");

            Assert.Throws<IOException>(() => LinuxAtomicFileCommit.Exchange(sourcePath, destinationPath));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(sourcePath), Is.EqualTo("remote"));
                Assert.That(File.Exists(destinationPath), Is.False);
            });
        }

        [Test]
        public async Task MoveNoReplace_PreservesDestinationAndSourceWhenDestinationAlreadyExists()
        {
            string sourcePath = Path.Combine(_root, "удалённый.txt");
            string destinationPath = Path.Combine(_root, "локальный.txt");
            await File.WriteAllTextAsync(sourcePath, "remote");
            await File.WriteAllTextAsync(destinationPath, "local edit");

            Assert.Throws<IOException>(() => LinuxAtomicFileCommit.MoveNoReplace(sourcePath, destinationPath));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(sourcePath), Is.EqualTo("remote"));
                Assert.That(File.ReadAllText(destinationPath), Is.EqualTo("local edit"));
            });
        }

        [Test]
        public async Task MoveNoReplace_ConcurrentCommitsPreserveWinnerAndLoserBytes()
        {
            for (int iteration = 0; iteration < ConcurrentCommitIterations; iteration++)
            {
                string directory = Path.Combine(_root, iteration.ToString());
                Directory.CreateDirectory(directory);
                string firstPath = Path.Combine(directory, "first.txt");
                string secondPath = Path.Combine(directory, "second.txt");
                string targetPath = Path.Combine(directory, "target.txt");
                await File.WriteAllTextAsync(firstPath, "first version");
                await File.WriteAllTextAsync(secondPath, "second version");
                TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> firstCommit = CommitNewFileAsync(firstPath, targetPath, start.Task);
                Task<bool> secondCommit = CommitNewFileAsync(secondPath, targetPath, start.Task);

                start.SetResult();
                bool[] completed = await Task.WhenAll(firstCommit, secondCommit);
                string[] contents = Directory.GetFiles(directory).Select(File.ReadAllText).ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(completed.Count(static succeeded => succeeded), Is.EqualTo(1));
                    Assert.That(File.Exists(targetPath), Is.True);
                    Assert.That(contents, Is.EquivalentTo(new[] { "first version", "second version" }));
                });
            }
        }

        [Test]
        public async Task Exchange_ConcurrentEditorSaveAlwaysRetainsEditedBytes()
        {
            for (int iteration = 0; iteration < ConcurrentCommitIterations; iteration++)
            {
                string directory = Path.Combine(_root, iteration.ToString());
                Directory.CreateDirectory(directory);
                string targetPath = Path.Combine(directory, "target.txt");
                string recoveryPath = Path.Combine(directory, "recovery.txt");
                string editorPath = Path.Combine(directory, "editor-save.txt");
                await File.WriteAllTextAsync(targetPath, "original");
                await File.WriteAllTextAsync(recoveryPath, "remote");
                await File.WriteAllTextAsync(editorPath, "local edit");
                TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task commit = ExchangeAfterStartAsync(recoveryPath, targetPath, start.Task);
                Task save = SaveAfterStartAsync(editorPath, targetPath, start.Task);

                start.SetResult();
                await Task.WhenAll(commit, save);

                Assert.That(
                    new[] { File.ReadAllText(targetPath), File.ReadAllText(recoveryPath) },
                    Is.EqualTo(new[] { "remote", "local edit" }).Or.EqualTo(new[] { "local edit", "original" }));
            }
        }

        private static async Task<bool> CommitNewFileAsync(string sourcePath, string targetPath, Task start)
        {
            await start;
            try
            {
                LinuxAtomicFileCommit.MoveNoReplace(sourcePath, targetPath);
                return true;
            }
            catch (IOException exception)
            {
                Trace.TraceInformation("Concurrent local file commit was rejected: {0}", exception.Message);
                return false;
            }
        }

        private static async Task ExchangeAfterStartAsync(string sourcePath, string targetPath, Task start)
        {
            await start;
            LinuxAtomicFileCommit.Exchange(sourcePath, targetPath);
        }

        private static async Task SaveAfterStartAsync(string sourcePath, string targetPath, Task start)
        {
            await start;
            File.Move(sourcePath, targetPath, overwrite: true);
        }

        private static async Task WriteRemoteContentAsync(Stream stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("another remote file"u8.ToArray(), cancellationToken);
        }
    }
}
