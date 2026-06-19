// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Cli
{
    internal static class SyncCliCrudSmokeCommandRunner
    {
        private const int MaxFinalConvergencePasses = 6;
        private static readonly string LocalUploadPath = "local-upload.txt";
        private static readonly string LocalRenamedPath = "local-renamed.txt";
        private static readonly string RemoteOriginPath = "remote-origin.txt";
        private static readonly string RemoteRenamedPath = "remote-renamed.txt";

        public static async Task<int> RunAsync(
            IReadOnlyList<string> args,
            TextWriter output,
            TextWriter error,
            HttpClient? injectedHttpClient,
            CancellationToken cancellationToken)
        {
            SyncCliConnectionOptions? firstOptions = SyncCliOptionsReader.ReadConnectionOptions(
                args,
                error,
                "sync-crud-smoke",
                allowBrowserLogin: true);
            if (firstOptions is null)
            {
                return 2;
            }

            SyncCliConnectionOptions? secondOptions = ReadSecondClientOptions(args, firstOptions, error);
            if (secondOptions is null)
            {
                return 2;
            }

            string? localRootError = ValidateLocalRoots(firstOptions.LocalRoot, secondOptions.LocalRoot);
            if (localRootError is not null)
            {
                await error.WriteLineAsync(localRootError).ConfigureAwait(false);
                return 2;
            }

            using HttpClient? ownedHttpClient = injectedHttpClient is null ? new HttpClient() : null;
            HttpClient httpClient = injectedHttpClient ?? ownedHttpClient!;
            try
            {
                Directory.CreateDirectory(firstOptions.LocalRoot);
                Directory.CreateDirectory(secondOptions.LocalRoot);
                await using SyncCliRuntime firstRuntime = await CreateRuntimeAsync(
                    firstOptions,
                    httpClient,
                    output,
                    cancellationToken).ConfigureAwait(false);
                await using SyncCliRuntime secondRuntime = await CreateRuntimeAsync(
                    secondOptions,
                    httpClient,
                    output,
                    cancellationToken).ConfigureAwait(false);

                await output.WriteLineAsync("Cotton Sync CRUD smoke").ConfigureAwait(false);
                await output.WriteLineAsync("Sync pair: " + firstOptions.SyncPairId).ConfigureAwait(false);
                await output.WriteLineAsync("Second sync pair: " + secondOptions.SyncPairId).ConfigureAwait(false);
                await output.WriteLineAsync("Remote root: " + FormatRemoteRoot(firstOptions)).ConfigureAwait(false);
                await output.WriteLineAsync("Local root: " + firstOptions.LocalRoot).ConfigureAwait(false);
                await output.WriteLineAsync("Second local root: " + secondOptions.LocalRoot).ConfigureAwait(false);

                int failures = 0;
                failures += await RunInitialConvergenceAsync(firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientACreateAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientBCreateAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientARenameAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientBRenameAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientADeleteAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);
                failures += await RunClientBDeleteAsync(firstOptions, secondOptions, firstRuntime, secondRuntime, output, cancellationToken)
                    .ConfigureAwait(false);

                SyncCliPassResult finalFirst = await RunFinalConvergenceAsync(firstRuntime, cancellationToken)
                    .ConfigureAwait(false);
                SyncCliPassResult finalSecond = await RunFinalConvergenceAsync(secondRuntime, cancellationToken)
                    .ConfigureAwait(false);
                int finalActivities = GetActivityCount(finalFirst) + GetActivityCount(finalSecond);
                int finalStateEntries = finalFirst.StateEntries.Count + finalSecond.StateEntries.Count;
                if (finalActivities != 0)
                {
                    failures++;
                }

                await output.WriteLineAsync("Final convergence activities: " + finalActivities.ToStringInvariant())
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Final state entries: " + finalStateEntries.ToStringInvariant())
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Converged: " + (failures == 0 ? "yes" : "no")).ConfigureAwait(false);
                await output.WriteLineAsync("Failures: " + failures.ToStringInvariant()).ConfigureAwait(false);
                return failures == 0 ? 0 : 1;
            }
            catch (AppCodeBrowserSignInException exception)
            {
                await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(exception.Error))
                {
                    await error.WriteLineAsync("Error: " + exception.Error).ConfigureAwait(false);
                }

                return 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await error.WriteLineAsync("sync-crud-smoke failed.").ConfigureAwait(false);
                await error.WriteLineAsync("Error: " + exception.GetType().Name + ": " + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
        }

        private static Task<SyncCliRuntime> CreateRuntimeAsync(
            SyncCliConnectionOptions options,
            HttpClient httpClient,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            return options.UseBrowserLogin
                ? SyncCliRuntimeFactory.CreateWithBrowserAuthAsync(
                    options,
                    httpClient,
                    new SyncCliApprovalUrlWriter(output),
                    cancellationToken)
                : SyncCliRuntimeFactory.CreateAsync(options, httpClient, cancellationToken);
        }

        private static async Task<int> RunInitialConvergenceAsync(
            SyncCliRuntime firstRuntime,
            SyncCliRuntime secondRuntime,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            SyncCliPassResult first = await SyncCliRuntimeFactory.RunSinglePassAsync(firstRuntime, cancellationToken)
                .ConfigureAwait(false);
            SyncCliPassResult second = await SyncCliRuntimeFactory.RunSinglePassAsync(secondRuntime, cancellationToken)
                .ConfigureAwait(false);
            int activities = GetActivityCount(first) + GetActivityCount(second);
            await output.WriteLineAsync("PASS: Initial sync reached idle/up-to-date. activities=" + activities.ToStringInvariant())
                .ConfigureAwait(false);
            return 0;
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
            SyncCliPassResult? lastPass = null;
            for (int pass = 1; pass <= MaxFinalConvergencePasses; pass++)
            {
                lastPass = await SyncCliRuntimeFactory.RunSinglePassAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (IsIdle(lastPass))
                {
                    return lastPass;
                }
            }

            return lastPass ?? throw new InvalidOperationException("Final convergence pass did not run.");
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

        private static string FullPath(string localRoot, string relativePath)
        {
            return Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static SyncCliConnectionOptions? ReadSecondClientOptions(
            IReadOnlyList<string> args,
            SyncCliConnectionOptions firstOptions,
            TextWriter error)
        {
            string? localRoot = SyncCliOptionsReader.ReadOption(args, "--second-local-root");
            string? syncPairId = SyncCliOptionsReader.ReadOption(args, "--second-sync-pair");
            string? databasePath = SyncCliOptionsReader.ReadOption(args, "--second-database");
            if (string.IsNullOrWhiteSpace(localRoot)
                || string.IsNullOrWhiteSpace(syncPairId)
                || string.IsNullOrWhiteSpace(databasePath))
            {
                error.WriteLine(
                    "sync-crud-smoke requires --second-local-root, --second-sync-pair, and --second-database.");
                return null;
            }

            if (string.Equals(firstOptions.SyncPairId, syncPairId.Trim(), StringComparison.Ordinal))
            {
                error.WriteLine("sync-crud-smoke sync pair ids must be different.");
                return null;
            }

            if (IsSamePath(firstOptions.DatabasePath, databasePath))
            {
                error.WriteLine("sync-crud-smoke databases must be different.");
                return null;
            }

            return firstOptions with
            {
                LocalRoot = localRoot,
                SyncPairId = syncPairId.Trim(),
                DatabasePath = databasePath,
            };
        }

        private static string? ValidateLocalRoots(string firstRoot, string secondRoot)
        {
            if (IsSameOrNestedPath(firstRoot, secondRoot))
            {
                return "sync-crud-smoke local roots must be different and non-nested.";
            }

            string? firstNonEmpty = ValidateEmptyOrMissingDirectory(firstRoot, "--local-root");
            if (firstNonEmpty is not null)
            {
                return firstNonEmpty;
            }

            return ValidateEmptyOrMissingDirectory(secondRoot, "--second-local-root");
        }

        private static string? ValidateEmptyOrMissingDirectory(string path, string optionName)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            return Directory.EnumerateFileSystemEntries(path).Any()
                ? optionName + " must be empty or missing because sync-crud-smoke creates, renames, and deletes files inside it."
                : null;
        }

        private static bool IsSameOrNestedPath(string firstPath, string secondPath)
        {
            string first = NormalizeFullPath(firstPath);
            string second = NormalizeFullPath(secondPath);
            StringComparison comparison = GetPathComparison();
            return string.Equals(first, second, comparison)
                || second.StartsWith(EnsureTrailingSeparator(first), comparison)
                || first.StartsWith(EnsureTrailingSeparator(second), comparison);
        }

        private static bool IsSamePath(string firstPath, string secondPath)
        {
            return string.Equals(NormalizeFullPath(firstPath), NormalizeFullPath(secondPath), GetPathComparison());
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, GetPathComparison()))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static StringComparison GetPathComparison()
        {
            return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private static string FormatRemoteRoot(SyncCliConnectionOptions options)
        {
            return options.RemoteRootNodeId?.ToString("D") ?? options.RemoteRootPath ?? "<not resolved>";
        }

        private static string CleanSingleLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Operation could not be completed.";
            }

            return message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static bool IsIdle(SyncCliPassResult pass)
        {
            return GetActivityCount(pass) == 0 && !pass.Result.HasDeferredLocalPaths;
        }

        private static int GetActivityCount(SyncCliPassResult pass)
        {
            return pass.Result.TotalActivityCount;
        }
    }
}
