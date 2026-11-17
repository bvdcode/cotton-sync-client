// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Live
{
    [Platform("Linux")]
    [NonParallelizable]
    public class LinuxLiveDownloadEditTests
    {
        [TestCase(false), TestCase(true), Category("LiveServer"), Category("Stress")]
        public async Task LocalEditDuringRemoteDownloadPreservesBothVersions(bool atomicSave)
        {
            if (Environment.GetEnvironmentVariable("COTTON_SYNC_LIVE_STRESS_TEST") != "1")
            {
                Assert.Ignore("Live download editing requires an explicitly enabled dedicated account and evidence directory.");
            }
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(20));
            CancellationToken token = timeout.Token;
            await using LiveStressSession session = new();
            const string path = "editing/source.bin";
            const long size = 64L * 1024 * 1024;
            string firstPath = LiveStressFiles.FullPath(session.FirstRoot, path);
            string secondPath = LiveStressFiles.FullPath(session.SecondRoot, path);
            string initialHash = await LiveStressFiles.WriteAsync(firstPath, size, token);
            await session.StartAsync(token);
            await session.ConvergeAsync(new Dictionary<string, string> { [path] = initialHash }, token);
            string staging = Path.Combine(session.DirectoryPath, "remote-update.bin");
            string remoteHash = await LiveStressFiles.WriteAsync(staging, size, token);
            string editedHash;
            session.SecondGate.Arm("/content");
            try
            {
                File.Move(staging, firstPath, overwrite: true);
                await session.SecondGate.WaitUntilBlockedAsync(token);
                DateTime originalWriteTime = File.GetLastWriteTimeUtc(secondPath);
                if (atomicSave)
                {
                    string editedStaging = Path.Combine(session.DirectoryPath, "local-edit.bin");
                    editedHash = await LiveStressFiles.WriteAsync(editedStaging, size, token);
                    File.Move(editedStaging, secondPath, overwrite: true);
                }
                else
                {
                    editedHash = await LiveStressFiles.WriteAsync(secondPath, size, token);
                }
                File.SetLastWriteTimeUtc(secondPath, originalWriteTime);
                TestContext.Progress.WriteLine($"Edited the receiving file during an active download; atomicSave={atomicSave}, size and timestamp retained.");
            }
            finally
            {
                session.SecondGate.Release();
            }
            await VerifyBothVersionsAsync(session, remoteHash, editedHash, token);
        }

        private static async Task VerifyBothVersionsAsync(LiveStressSession session,
            string remoteHash, string editedHash, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                Dictionary<string, string> observed = new(StringComparer.Ordinal);
                foreach (string file in Directory.EnumerateFiles(session.SecondRoot, "*", SearchOption.AllDirectories))
                {
                    string path = Path.GetRelativePath(session.SecondRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (!SyncPathIgnoreRules.ShouldIgnore(path))
                    {
                        observed[path] = await LiveStressFiles.HashAsync(file, token);
                    }
                }
                if (observed.Count == 2 && observed.Values.ToHashSet(StringComparer.Ordinal).SetEquals([remoteHash, editedHash]))
                {
                    TestContext.Progress.WriteLine("The receiving client preserved both exact versions of the concurrent edit.");
                    await ResolveConflictAsync(session, observed, remoteHash, editedHash, token);
                    await session.ConvergeAsync(observed, token);
                    TestContext.Progress.WriteLine("After keeping the local edit under a separate name, both exact versions synchronized to both clients.");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            Assert.Fail("The remote update and receiving client's edit did not both survive on the receiving client.");
        }

        private static async Task ResolveConflictAsync(LiveStressSession session,
            Dictionary<string, string> observed, string remoteHash, string editedHash, CancellationToken token)
        {
            string remotePath = observed.Single(entry => entry.Value == remoteHash).Key;
            string editedPath = observed.Single(entry => entry.Value == editedHash).Key;
            const string originalPath = "editing/source.bin";
            const string preservedEditPath = "editing/local-version.bin";
            string staging = Path.Combine(session.DirectoryPath, "resolved-local-version.bin");
            File.Copy(LiveStressFiles.FullPath(session.SecondRoot, editedPath), staging);
            File.Move(staging, LiveStressFiles.FullPath(session.SecondRoot, preservedEditPath));
            if (remotePath != originalPath)
            {
                File.Copy(LiveStressFiles.FullPath(session.SecondRoot, remotePath),
                    LiveStressFiles.FullPath(session.SecondRoot, originalPath), overwrite: true);
            }
            observed[originalPath] = remoteHash;
            observed[preservedEditPath] = editedHash;
            Assert.That(await LiveStressFiles.HashAsync(
                LiveStressFiles.FullPath(session.SecondRoot, preservedEditPath), token), Is.EqualTo(editedHash));
        }
    }
}
