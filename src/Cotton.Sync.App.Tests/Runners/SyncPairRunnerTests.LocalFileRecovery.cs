// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        [Test]
        public async Task SyncNowAsync_RetriesUnavailableLocalFileAndReturnsIdleOnRecovery()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new LocalFileUnavailableException(
                        "writing.txt",
                        "/home/user/Cotton/writing.txt",
                        "the file changed during scanning."),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public void SyncNowAsync_DoesNotWaitIndefinitelyForNonTransientUnavailableFile()
        {
            LocalFileUnavailableException unavailable = new(
                "missing.txt",
                "/home/user/Cotton/missing.txt",
                "the file no longer exists.");
            FakeSyncPairWork work = new()
            {
                Failure = unavailable,
            };
            SyncPairRunner runner = CreateRunner(
                CreatePair(isEnabled: true),
                work,
                NoDelayRetryOptions(maxAttempts: 2));

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.SameAs(unavailable));
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
            });
        }

        [Test]
        public async Task SyncNowAsync_WaitsForPersistentlyLockedLocalFileWithoutActionRequiredAndRecovers()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            LocalFileUnavailableException unavailable = new LocalFileUnavailableException(
                "locked.txt",
                filePath,
                new IOException("The file is being used by another process."),
                requiresExclusiveAccess: true);
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [unavailable, unavailable],
            };
            SyncPairRunnerRetryOptions retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(10),
            };
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, retryOptions, logger);

            try
            {
                Task sync = runner.SyncNowAsync();
                for (int attempt = 0; attempt < 200 && runner.Status.State != SyncPairRunState.Waiting; attempt++)
                {
                    await Task.Delay(5);
                }

                SyncPairStatus waiting = runner.Status;
                Assert.Multiple(() =>
                {
                    Assert.That(sync.IsCompleted, Is.False);
                    Assert.That(waiting.State, Is.EqualTo(SyncPairRunState.Waiting));
                    Assert.That(waiting.LastError, Does.Contain("locked.txt"));
                    Assert.That(waiting.CurrentOperation, Does.Not.StartWith("Action required"));
                    Assert.That(
                        logger.Entries.Select(entry => entry.Message),
                        Has.None.Contains("Sync pair runner failed"));
                });

                locked.Dispose();
                locked = null;
                await sync.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(3));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(runner.Status.LastError, Is.Null);
                    Assert.That(runner.Status.CurrentOperation, Is.Null);
                });
            }
            finally
            {
                locked?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SyncNowAsync_WaitsForExclusiveAccessRequiredByPlaceholderFinalization()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "open-in-excel.xlsx");
            File.WriteAllText(filePath, "workbook");
            FileStream? openWorkbook = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            LocalFileUnavailableException unavailable = new(
                "open-in-excel.xlsx",
                filePath,
                new IOException("The file is being used by another process."),
                requiresExclusiveAccess: true);
            FakeSyncPairWork work = new()
            {
                Failures = [unavailable],
            };
            SyncPairRunnerRetryOptions retryOptions = new()
            {
                MaxAttempts = 1,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(10),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, retryOptions);

            try
            {
                Task sync = runner.SyncNowAsync();
                for (int attempt = 0; attempt < 200 && runner.Status.State != SyncPairRunState.Waiting; attempt++)
                {
                    await Task.Delay(5);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(sync.IsCompleted, Is.False);
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Waiting));
                    Assert.That(runner.Status.LastError, Does.Contain("open-in-excel.xlsx"));
                    Assert.That(runner.Status.CurrentOperation, Does.Not.StartWith("Action required"));
                });

                openWorkbook.Dispose();
                openWorkbook = null;
                await sync.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(2));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(runner.Status.LastError, Is.Null);
                });
            }
            finally
            {
                openWorkbook?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task PauseAsync_CancelsLocalFileWaitAndPausesRunner()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            LocalFileUnavailableException unavailable = new LocalFileUnavailableException(
                "locked.txt",
                filePath,
                new IOException("The file is being used by another process."),
                requiresExclusiveAccess: true);
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [unavailable],
            };
            SyncPairRunnerRetryOptions retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 1,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, retryOptions);

            try
            {
                Task sync = runner.SyncNowAsync();
                for (int attempt = 0; attempt < 200 && runner.Status.State != SyncPairRunState.Waiting; attempt++)
                {
                    await Task.Delay(5);
                }

                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Waiting));

                await runner.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await sync.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(1));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
                    Assert.That(runner.Status.LastError, Is.Null);
                });
            }
            finally
            {
                locked.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SyncNowAsync_RetriesLockedLocalFileAfterItBecomesReadable()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            ReleasingLockedFileSyncPairWork work = new ReleasingLockedFileSyncPairWork(() =>
            {
                locked?.Dispose();
                locked = null;
            });
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, NoDelayRetryOptions());

            try
            {
                await runner.SyncNowAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(2));
                    Assert.That(work.ScannedPaths, Is.EqualTo(new[] { "locked.txt" }));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                });
            }
            finally
            {
                locked?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SyncNowAsync_RetriesMissingLocalRootAndReturnsIdleOnRecovery()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            RestoringMissingRootSyncPairWork work = new RestoringMissingRootSyncPairWork(root, () =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "restored.txt"), "restored");
            });
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, NoDelayRetryOptions());

            try
            {
                await runner.SyncNowAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(2));
                    Assert.That(work.ScannedPaths, Is.EqualTo(new[] { "restored.txt" }));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(runner.Status.LastError, Is.Null);
                });
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Test]
        public void SyncNowAsync_ReportsMissingLocalRootAsActionRequiredMessageWhenRestoreDoesNotHappen()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new DirectoryNotFoundException("Local sync root was not found: W:\\local"),
                    new DirectoryNotFoundException("Local sync root was not found: W:\\local"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            DirectoryNotFoundException? exception = Assert.ThrowsAsync<DirectoryNotFoundException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

    }
}
