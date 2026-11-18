// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Tests.Live
{
    [Platform("Linux")]
    [NonParallelizable]
    public class LinuxLiveSyncStressTests
    {
        [Test, Category("LiveServer"), Category("Stress")]
        public async Task LargeFileAndManyFilesConverge()
        {
            if (Environment.GetEnvironmentVariable("COTTON_SYNC_LIVE_STRESS_TEST") != "1")
            {
                Assert.Ignore("Live load testing requires an explicitly enabled dedicated account and evidence directory.");
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromHours(4));
            CancellationToken token = timeout.Token;
            await using LiveStressSession session = new();
            Dictionary<string, string> expected = new(StringComparer.Ordinal);
            Stopwatch elapsed = Stopwatch.StartNew();
            const int smallFileCount = 2000;
            const long largeFileSize = 1024L * 1024 * 1024;
            for (int index = 0; index < smallFileCount; index++)
            {
                string relativePath = $"burst/group-{index % 20:D2}/данные-{index:D5}.bin";
                string root = index % 2 == 0 ? session.FirstRoot : session.SecondRoot;
                expected[relativePath] = await LiveStressFiles.WriteAsync(
                    LiveStressFiles.FullPath(root, relativePath), 1024 + index, token);
            }
            expected["large/random-1GiB.bin"] = await LiveStressFiles.WriteAsync(
                LiveStressFiles.FullPath(session.FirstRoot, "large/random-1GiB.bin"), largeFileSize, token);
            await session.StartAsync(token, largeFileSize + (long)smallFileCount * (1024 + smallFileCount));
            await session.ConvergeAsync(expected, token, TimeSpan.FromMinutes(90));
            TestContext.Progress.WriteLine($"Initial load passed: {smallFileCount} small files and {largeFileSize} random bytes; {elapsed.Elapsed}.");

            TestContext.Progress.WriteLine($"Load passed in {elapsed.Elapsed}; peak working set: {Process.GetCurrentProcess().PeakWorkingSet64} bytes.");
        }

        [Test, Category("LiveServer"), Category("Stress")]
        public async Task ActiveTransfersRecoverFromMutationsRestartAndNetworkLoss()
        {
            if (Environment.GetEnvironmentVariable("COTTON_SYNC_LIVE_STRESS_TEST") != "1")
            {
                Assert.Ignore("Live transfer testing requires an explicitly enabled dedicated account and evidence directory.");
            }
            using CancellationTokenSource timeout = new(TimeSpan.FromHours(2));
            CancellationToken token = timeout.Token;
            await using LiveStressSession session = new();
            Dictionary<string, string> expected = new(StringComparer.Ordinal);
            Stopwatch elapsed = Stopwatch.StartNew();
            await session.StartAsync(token);
            await session.ConvergeAsync(expected, token);
            foreach (string action in new[] { "overwrite", "delete", "rename", "rename-parent" })
            {
                await MutateDuringUploadAsync(session, expected, action, token);
            }
            foreach (string action in new[] { "overwrite", "delete", "rename", "rename-parent" })
            {
                await MutateDuringDownloadAsync(session, expected, action, token);
            }
            await RestartDuringUploadAsync(session, expected, token);
            session.FirstGate.SetOffline(true);
            expected["offline/first.bin"] = await LiveStressFiles.WriteAsync(
                LiveStressFiles.FullPath(session.FirstRoot, "offline/first.bin"), 12345, token);
            expected["offline/second.bin"] = await LiveStressFiles.WriteAsync(
                LiveStressFiles.FullPath(session.SecondRoot, "offline/second.bin"), 23456, token);
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            session.FirstGate.SetOffline(false);
            await session.ConvergeAsync(expected, token);
            TestContext.Progress.WriteLine("Automatic recovery after network loss and changes on both clients passed.");
            await session.First.PauseAllAsync(token);
            expected["after-pause.txt"] = await LiveStressFiles.WriteAsync(
                LiveStressFiles.FullPath(session.FirstRoot, "after-pause.txt"), 8192, token);
            await session.First.ResumeAllAsync(token);
            await session.ConvergeAsync(expected, token);
            TestContext.Progress.WriteLine($"Load, eight in-flight mutations, restart, network recovery and pause/resume passed in {elapsed.Elapsed}.");
            TestContext.Progress.WriteLine($"Test process peak working set: {Process.GetCurrentProcess().PeakWorkingSet64} bytes.");
        }

        private static async Task RestartDuringUploadAsync(LiveStressSession session,
            Dictionary<string, string> expected, CancellationToken token)
        {
            const string path = "restart/active.bin";
            string staging = Path.Combine(session.DirectoryPath, "staging.bin");
            expected[path] = await LiveStressFiles.WriteAsync(staging, 64L * 1024 * 1024, token);
            string destination = LiveStressFiles.FullPath(session.FirstRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            session.FirstGate.Arm("/chunks/raw");
            try
            {
                File.Move(staging, destination);
                await session.FirstGate.WaitUntilBlockedAsync(token);
                await session.RestartFirstAsync(token);
            }
            finally
            {
                session.FirstGate.Release();
            }
            await session.ConvergeAsync(expected, token);
            TestContext.Progress.WriteLine("Restart during upload preserved the session and exact file content.");
        }

        private static async Task MutateDuringUploadAsync(LiveStressSession session,
            Dictionary<string, string> expected, string action, CancellationToken token)
        {
            string path = $"upload-{action}/source.bin";
            string staging = Path.Combine(session.DirectoryPath, "staging.bin");
            string hash = await LiveStressFiles.WriteAsync(staging, 64L * 1024 * 1024, token);
            string source = LiveStressFiles.FullPath(session.FirstRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            session.FirstGate.Arm("/chunks/raw");
            try
            {
                File.Move(staging, source);
                await session.FirstGate.WaitUntilBlockedAsync(token);
                TestContext.Progress.WriteLine("Active upload confirmed after a completed chunk: " + action);
                await ApplyMutationAsync(session.FirstRoot, path, hash, action, expected, token);
            }
            finally
            {
                session.FirstGate.Release();
            }
            await session.ConvergeAsync(expected, token);
            TestContext.Progress.WriteLine("In-flight upload mutation passed: " + action);
        }

        private static async Task MutateDuringDownloadAsync(LiveStressSession session,
            Dictionary<string, string> expected, string action, CancellationToken token)
        {
            string path = $"download-{action}/source.bin";
            string staging = Path.Combine(session.DirectoryPath, "staging.bin");
            string hash = await LiveStressFiles.WriteAsync(staging, 64L * 1024 * 1024, token);
            string source = LiveStressFiles.FullPath(session.FirstRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            session.SecondGate.Arm("/content");
            try
            {
                File.Move(staging, source);
                await session.SecondGate.WaitUntilBlockedAsync(token);
                TestContext.Progress.WriteLine("Active download confirmed after a completed chunk: " + action);
                await ApplyMutationAsync(session.FirstRoot, path, hash, action, expected, token);
            }
            finally
            {
                session.SecondGate.Release();
            }
            await session.ConvergeAsync(expected, token);
            TestContext.Progress.WriteLine("In-flight download mutation passed: " + action);
        }

        private static async Task ApplyMutationAsync(string root, string path, string originalHash,
            string action, Dictionary<string, string> expected, CancellationToken token)
        {
            string fullPath = LiveStressFiles.FullPath(root, path);
            switch (action)
            {
                case "overwrite":
                    expected[path] = await LiveStressFiles.WriteAsync(fullPath, (17L * 1024 * 1024) + 31, token);
                    break;
                case "delete":
                    File.Delete(fullPath);
                    break;
                case "rename":
                    string renamedPath = path.Replace("source.bin", "переименован.bin", StringComparison.Ordinal);
                    File.Move(fullPath, LiveStressFiles.FullPath(root, renamedPath));
                    expected[renamedPath] = originalHash;
                    break;
                case "rename-parent":
                    string oldParent = Path.GetDirectoryName(fullPath)!;
                    Directory.Move(oldParent, oldParent + "-renamed");
                    expected[Path.GetDirectoryName(path)!.Replace('\\', '/') + "-renamed/source.bin"] = originalHash;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported mutation.");
            }
        }
    }
}
