// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Sync;
using Cotton.Sync.Cli;
using Cotton.Sync.Cli.Tests.TestSupport;
using Cotton.Sync.State;

namespace Cotton.Sync.Cli.Tests
{
    public partial class SyncCliCommandRunnerTests
    {
        private static string[] CreateSyncOnceProcessArgs(
            Uri serverUri,
            string localRoot,
            Guid remoteRootId,
            string syncPairId,
            string databasePath)
        {
            return
            [
                "sync-once",
                "--server",
                serverUri.AbsoluteUri,
                "--username",
                "testuser",
                "--password",
                "testpassword",
                "--local-root",
                localRoot,
                "--remote-root",
                remoteRootId.ToString("D"),
                "--sync-pair",
                syncPairId,
                "--database",
                databasePath,
            ];
        }

        private static SyncCliConvergenceResult CreateCrudSmokeConvergence(bool converged)
        {
            return new SyncCliConvergenceResult(
                new SyncCliPassResult(new SyncRunResult(), []),
                converged,
                Passes: 1);
        }

        private static Process StartCliProcess(IEnumerable<string> args)
        {
            string cliPath = typeof(SyncCliCommandRunner).Assembly.Location;
            ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(cliPath);
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Cotton Sync CLI process.");
        }

        private static async Task WaitForProcessExitAsync(Process process, TimeSpan timeout)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                KillProcessTree(process);
                throw new AssertionException("Cotton Sync CLI process did not exit within " + timeout.TotalSeconds.ToStringInvariant() + " seconds.", exception);
            }
        }

        private static async Task WaitForTemporaryDownloadAsync(string temporaryDirectory, TimeSpan timeout)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (ListTemporaryDownloads(temporaryDirectory).Length > 0)
                    {
                        return;
                    }

                    await Task.Delay(25, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            throw new AssertionException("Cotton Sync CLI process did not create a temporary download file within "
                + timeout.TotalSeconds.ToStringInvariant()
                + " seconds.");
        }

        private static string[] ListTemporaryDownloads(string temporaryDirectory)
        {
            return Directory.Exists(temporaryDirectory)
                ? Directory.GetFiles(temporaryDirectory, "*.download", SearchOption.AllDirectories)
                : [];
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string? ReadStartRequestProperty(string body, string propertyName)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(propertyName, out JsonElement property)
                ? property.GetString()
                : null;
        }
    }
}
