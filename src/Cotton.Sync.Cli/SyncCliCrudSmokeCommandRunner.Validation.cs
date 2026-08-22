// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliCrudSmokeCommandRunner
    {

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

            if (SyncCliPath.AreSame(firstOptions.DatabasePath, databasePath))
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
            if (SyncCliPath.AreSameOrNested(firstRoot, secondRoot))
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


        private static int GetDeferredLocalPathCount(SyncCliPassResult pass)
        {
            return pass.Result.DeferredLocalPaths.Count;
        }


        private static string FormatYesNo(bool value)
        {
            return value ? "yes" : "no";
        }
    }
}
