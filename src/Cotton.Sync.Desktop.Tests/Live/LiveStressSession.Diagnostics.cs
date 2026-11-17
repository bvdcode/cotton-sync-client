// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal partial class LiveStressSession
    {
        private async Task WriteConvergenceFailureAsync(
            IReadOnlyDictionary<string, string> expected,
            CancellationToken cancellationToken)
        {
            DesktopShellSnapshot first = await First.LoadAsync(cancellationToken);
            DesktopShellSnapshot second = await Second.LoadAsync(cancellationToken);
            await TestContext.Out.WriteLineAsync(
                $"Convergence failed: first={first.SyncPairs.Single().Status}, second={second.SyncPairs.Single().Status}, "
                + $"firstError={first.SyncPairs.Single().LastError}, secondError={second.SyncPairs.Single().LastError}");
            await WriteFileDifferencesAsync("first", FirstRoot, expected, cancellationToken);
            await WriteFileDifferencesAsync("second", SecondRoot, expected, cancellationToken);
        }

        private static async Task WriteFileDifferencesAsync(
            string client,
            string root,
            IReadOnlyDictionary<string, string> expected,
            CancellationToken cancellationToken)
        {
            string[] paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !SyncPathIgnoreRules.ShouldIgnore(path))
                .Order(StringComparer.Ordinal).ToArray();
            string[] allPaths = expected.Keys.Union(paths, StringComparer.Ordinal).ToArray();
            IReadOnlyDictionary<string, LiveSyncSmokeFileHashReadResult> reads =
                await LiveSyncSmokeFileHashReader.ReadAsync(
                    allPaths.Select(path => LiveStressFiles.FullPath(root, path)), cancellationToken);
            foreach (string path in allPaths)
            {
                expected.TryGetValue(path, out string? expectedHash);
                LiveSyncSmokeFileHashReadResult read = reads[LiveStressFiles.FullPath(root, path)];
                if (expectedHash != read.Sha256 || read.Error is not null)
                {
                    await TestContext.Out.WriteLineAsync(
                        $"File mismatch: client={client}, path={path}, expected={expectedHash ?? "<absent>"}, "
                        + $"actual={read.Sha256 ?? "<unavailable>"}, readError={read.Error}");
                }
            }
        }
    }
}
