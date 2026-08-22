// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliCrudSmokeCommandRunner
    {

        private static async Task<int> RunInitialConvergenceAsync(
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            SyncCliConvergenceResult first = await RunConvergenceAsync(firstRuntime, cancellationToken)
                .ConfigureAwait(false);
            SyncCliConvergenceResult second = await RunConvergenceAsync(secondRuntime, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(FormatInitialConvergenceLine(first, second)).ConfigureAwait(false);
            return first.Converged && second.Converged ? 0 : 1;
        }


        private static async Task<int> RunClientACreateAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string content = "Cotton Sync CRUD smoke from client A" + Environment.NewLine
                + SyncCliFormat.FormatUtc(DateTime.UtcNow) + Environment.NewLine;
            await WriteFileAsync(firstOptions.LocalRoot, LocalUploadPath, content, cancellationToken).ConfigureAwait(false);
            await RunSourceThenTargetAsync(firstRuntime, secondRuntime, cancellationToken).ConfigureAwait(false);
            return await VerifyPresentAsync(
                firstOptions,
                secondOptions,
                LocalUploadPath,
                content,
                "Local create uploaded and downloaded by the second client.",
                output,
                cancellationToken).ConfigureAwait(false);
        }


        private static async Task<int> RunClientBCreateAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string content = "Cotton Sync CRUD smoke from client B" + Environment.NewLine
                + SyncCliFormat.FormatUtc(DateTime.UtcNow) + Environment.NewLine;
            await WriteFileAsync(secondOptions.LocalRoot, RemoteOriginPath, content, cancellationToken).ConfigureAwait(false);
            await RunSourceThenTargetAsync(secondRuntime, firstRuntime, cancellationToken).ConfigureAwait(false);
            return await VerifyPresentAsync(
                firstOptions,
                secondOptions,
                RemoteOriginPath,
                content,
                "Remote-origin create downloaded by the first client.",
                output,
                cancellationToken).ConfigureAwait(false);
        }


        private static async Task<int> RunClientARenameAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string firstSource = FullPath(firstOptions.LocalRoot, LocalUploadPath);
            string firstTarget = FullPath(firstOptions.LocalRoot, LocalRenamedPath);
            File.Move(firstSource, firstTarget);
            await RunSourceThenTargetAsync(firstRuntime, secondRuntime, cancellationToken).ConfigureAwait(false);
            return VerifyRename(
                firstOptions,
                secondOptions,
                LocalUploadPath,
                LocalRenamedPath,
                "Local rename propagated to the second client.",
                output);
        }


        private static async Task<int> RunClientBRenameAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string secondSource = FullPath(secondOptions.LocalRoot, RemoteOriginPath);
            string secondTarget = FullPath(secondOptions.LocalRoot, RemoteRenamedPath);
            File.Move(secondSource, secondTarget);
            await RunSourceThenTargetAsync(secondRuntime, firstRuntime, cancellationToken).ConfigureAwait(false);
            return VerifyRename(
                firstOptions,
                secondOptions,
                RemoteOriginPath,
                RemoteRenamedPath,
                "Remote-origin rename propagated to the first client.",
                output);
        }


        private static async Task<int> RunClientADeleteAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            File.Delete(FullPath(firstOptions.LocalRoot, LocalRenamedPath));
            await RunSourceThenTargetAsync(firstRuntime, secondRuntime, cancellationToken).ConfigureAwait(false);
            return VerifyAbsent(
                firstOptions,
                secondOptions,
                LocalRenamedPath,
                "Local delete propagated to the second client.",
                output);
        }


        private static async Task<int> RunClientBDeleteAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            File.Delete(FullPath(secondOptions.LocalRoot, RemoteRenamedPath));
            await RunSourceThenTargetAsync(secondRuntime, firstRuntime, cancellationToken).ConfigureAwait(false);
            return VerifyAbsent(
                firstOptions,
                secondOptions,
                RemoteRenamedPath,
                "Remote-origin delete propagated to the first client.",
                output);
        }


        private static async Task RunSourceThenTargetAsync(
            SyncCliRuntime sourceRuntime,
            SyncCliRuntime targetRuntime,
            CancellationToken cancellationToken)
        {
            await SyncCliRuntimeFactory.RunSinglePassAsync(sourceRuntime, cancellationToken).ConfigureAwait(false);
            await SyncCliRuntimeFactory.RunSinglePassAsync(targetRuntime, cancellationToken).ConfigureAwait(false);
            await RunFinalConvergenceAsync(sourceRuntime, cancellationToken).ConfigureAwait(false);
            await RunFinalConvergenceAsync(targetRuntime, cancellationToken).ConfigureAwait(false);
        }


        private static async Task<SyncCliPassResult> RunFinalConvergenceAsync(
            SyncCliRuntime runtime,
            CancellationToken cancellationToken)
        {
            SyncCliConvergenceResult result = await RunConvergenceAsync(runtime, cancellationToken).ConfigureAwait(false);
            return result.Pass;
        }


        private static async Task<SyncCliConvergenceResult> RunConvergenceAsync(
            SyncCliRuntime runtime,
            CancellationToken cancellationToken)
        {
            SyncCliPassResult? lastPass = null;
            for (int pass = 1; pass <= MaxFinalConvergencePasses; pass++)
            {
                lastPass = await SyncCliRuntimeFactory.RunSinglePassAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (IsIdle(lastPass))
                {
                    return new SyncCliConvergenceResult(lastPass, Converged: true, pass);
                }
            }

            return new SyncCliConvergenceResult(
                lastPass ?? throw new InvalidOperationException("Final convergence pass did not run."),
                Converged: false,
                MaxFinalConvergencePasses);
        }


        private static async Task WriteFileAsync(
            string localRoot,
            string relativePath,
            string content,
            CancellationToken cancellationToken)
        {
            string fullPath = FullPath(localRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }


        private static async Task<int> VerifyPresentAsync(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            string relativePath,
            string expectedContent,
            string label,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            string firstPath = FullPath(firstOptions.LocalRoot, relativePath);
            string secondPath = FullPath(secondOptions.LocalRoot, relativePath);
            bool firstExists = File.Exists(firstPath);
            bool secondExists = File.Exists(secondPath);
            string? firstContent = firstExists ? await File.ReadAllTextAsync(firstPath, cancellationToken).ConfigureAwait(false) : null;
            string? secondContent = secondExists ? await File.ReadAllTextAsync(secondPath, cancellationToken).ConfigureAwait(false) : null;
            bool passed = firstExists
                && secondExists
                && string.Equals(firstContent, expectedContent, StringComparison.Ordinal)
                && string.Equals(secondContent, expectedContent, StringComparison.Ordinal);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedContent)));
            await output.WriteLineAsync(
                    FormatCheck(passed, label)
                    + " path=" + relativePath
                    + ", sha256=" + hash)
                .ConfigureAwait(false);
            return passed ? 0 : 1;
        }


        private static int VerifyRename(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            string oldPath,
            string newPath,
            string label,
            TextWriter output)
        {
            bool passed = !File.Exists(FullPath(firstOptions.LocalRoot, oldPath))
                && !File.Exists(FullPath(secondOptions.LocalRoot, oldPath))
                && File.Exists(FullPath(firstOptions.LocalRoot, newPath))
                && File.Exists(FullPath(secondOptions.LocalRoot, newPath));
            output.WriteLine(FormatCheck(passed, label) + " oldPath=" + oldPath + ", newPath=" + newPath);
            return passed ? 0 : 1;
        }


        private static int VerifyAbsent(
            SyncCliConnectionOptions firstOptions,
            SyncCliConnectionOptions secondOptions,
            string relativePath,
            string label,
            TextWriter output)
        {
            bool passed = !File.Exists(FullPath(firstOptions.LocalRoot, relativePath))
                && !File.Exists(FullPath(secondOptions.LocalRoot, relativePath));
            output.WriteLine(FormatCheck(passed, label) + " path=" + relativePath);
            return passed ? 0 : 1;
        }


        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
        }


        internal static string FormatInitialConvergenceLine(
            SyncCliConvergenceResult first,
            SyncCliConvergenceResult second)
        {
            bool passed = first.Converged && second.Converged;
            return FormatCheck(passed, "Initial sync reached idle/up-to-date.")
                + " clientAActivities=" + GetActivityCount(first.Pass).ToStringInvariant()
                + ", clientADeferredLocalPaths=" + GetDeferredLocalPathCount(first.Pass).ToStringInvariant()
                + ", clientAStateEntries=" + first.Pass.StateEntries.Count.ToStringInvariant()
                + ", clientAPasses=" + first.Passes.ToStringInvariant()
                + ", clientAConverged=" + FormatYesNo(first.Converged)
                + ", clientBActivities=" + GetActivityCount(second.Pass).ToStringInvariant()
                + ", clientBDeferredLocalPaths=" + GetDeferredLocalPathCount(second.Pass).ToStringInvariant()
                + ", clientBStateEntries=" + second.Pass.StateEntries.Count.ToStringInvariant()
                + ", clientBPasses=" + second.Passes.ToStringInvariant()
                + ", clientBConverged=" + FormatYesNo(second.Converged);
        }


        private static string FullPath(string localRoot, string relativePath)
        {
            return Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
