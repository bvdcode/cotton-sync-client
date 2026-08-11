// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.State;

namespace Cotton.Sync.Cli
{
    internal static class SyncCliOptionsReader
    {
        public static SyncCliConnectionOptions? ReadConnectionOptions(
            IReadOnlyList<string> args,
            TextWriter error,
            string command,
            bool allowBrowserLogin = false)
        {
            SyncCliConnectionArguments arguments = ReadConnectionArguments(args, allowBrowserLogin);
            if (HasMissingConnectionOption(arguments))
            {
                error.WriteLine(
                    command
                    + " requires --server, --local-root, --remote-root or --remote-path, --sync-pair, and --database.");
                return null;
            }

            if (!HasValidSignInOptions(arguments, command, error))
            {
                return null;
            }

            Uri? serverUri = CottonServerUrl.NormalizeOptional(arguments.Server);
            if (serverUri is null)
            {
                error.WriteLine("--server must be an HTTP or HTTPS URL.");
                return null;
            }

            if (!TryReadRemoteRoot(arguments.RemoteRoot, arguments.RemotePath, error, out Guid? remoteRootNodeId))
            {
                return null;
            }

            return CreateConnectionOptions(arguments, serverUri, remoteRootNodeId);
        }

        private static SyncCliConnectionArguments ReadConnectionArguments(
            IReadOnlyList<string> args,
            bool allowBrowserLogin)
        {
            return new SyncCliConnectionArguments
            {
                DatabasePath = ReadOption(args, "--database"),
                LocalRoot = ReadOption(args, "--local-root"),
                Password = ReadPassword(args),
                RemotePath = ReadOption(args, "--remote-path"),
                RemoteRoot = ReadOption(args, "--remote-root"),
                Server = ReadOption(args, "--server"),
                SyncPairId = ReadOption(args, "--sync-pair"),
                TwoFactorCode = ReadOption(args, "--two-factor-code"),
                UseBrowserLogin = allowBrowserLogin && HasFlag(args, "--browser-login"),
                Username = ReadOption(args, "--username"),
            };
        }

        private static SyncCliConnectionOptions CreateConnectionOptions(
            SyncCliConnectionArguments arguments,
            Uri serverUri,
            Guid? remoteRootNodeId)
        {
            return new SyncCliConnectionOptions(
                serverUri,
                arguments.UseBrowserLogin ? null : arguments.Username!.Trim(),
                arguments.UseBrowserLogin ? null : arguments.Password!,
                arguments.LocalRoot!,
                remoteRootNodeId,
                NormalizeOptional(arguments.RemotePath),
                arguments.SyncPairId!.Trim(),
                arguments.DatabasePath!,
                NormalizeOptional(arguments.TwoFactorCode),
                arguments.UseBrowserLogin);
        }

        private static bool HasMissingConnectionOption(SyncCliConnectionArguments arguments)
        {
            return string.IsNullOrWhiteSpace(arguments.Server)
                || string.IsNullOrWhiteSpace(arguments.LocalRoot)
                || (string.IsNullOrWhiteSpace(arguments.RemoteRoot) && string.IsNullOrWhiteSpace(arguments.RemotePath))
                || string.IsNullOrWhiteSpace(arguments.SyncPairId)
                || string.IsNullOrWhiteSpace(arguments.DatabasePath);
        }

        private static bool HasValidSignInOptions(
            SyncCliConnectionArguments arguments,
            string command,
            TextWriter error)
        {
            if (arguments.UseBrowserLogin)
            {
                if (string.IsNullOrWhiteSpace(arguments.Username)
                    && string.IsNullOrWhiteSpace(arguments.Password)
                    && string.IsNullOrWhiteSpace(arguments.TwoFactorCode))
                {
                    return true;
                }

                error.WriteLine("--browser-login cannot be combined with password sign-in options.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(arguments.Username) && !string.IsNullOrWhiteSpace(arguments.Password))
            {
                return true;
            }

            error.WriteLine(
                command + " requires --username and --password or --password-env unless --browser-login is used.");
            return false;
        }

        private static bool TryReadRemoteRoot(
            string? remoteRoot,
            string? remotePath,
            TextWriter error,
            out Guid? remoteRootNodeId)
        {
            remoteRootNodeId = null;
            if (string.IsNullOrWhiteSpace(remoteRoot))
            {
                return true;
            }

            if (!Guid.TryParse(remoteRoot, out Guid parsedRemoteRootNodeId))
            {
                error.WriteLine("--remote-root must be a node id GUID.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(remotePath))
            {
                error.WriteLine("--remote-root and --remote-path cannot be used together.");
                return false;
            }

            remoteRootNodeId = parsedRemoteRootNodeId;
            return true;
        }

        public static SyncCliBrowserAuthOptions? ReadBrowserAuthOptions(
            IReadOnlyList<string> args,
            TextWriter error)
        {
            string? server = ReadOption(args, "--server");
            if (string.IsNullOrWhiteSpace(server))
            {
                error.WriteLine("auth-browser requires --server.");
                return null;
            }

            Uri? serverUri = CottonServerUrl.NormalizeOptional(server);
            if (serverUri is null)
            {
                error.WriteLine("--server must be an HTTP or HTTPS URL.");
                return null;
            }

            string applicationName = ReadOption(args, "--application-name")?.Trim() ?? "Cotton Sync CLI";
            if (string.IsNullOrWhiteSpace(applicationName))
            {
                error.WriteLine("--application-name must not be empty.");
                return null;
            }

            if (!TryReadOptionalPositiveInt(args, "--timeout-seconds", error, out int? timeoutSeconds))
            {
                return null;
            }

            return new SyncCliBrowserAuthOptions(
                serverUri,
                applicationName,
                NormalizeOptional(ReadOption(args, "--application-version")) ?? SyncCliAppVersion.Current,
                NormalizeOptional(ReadOption(args, "--device-name")) ?? "Cotton Sync CLI",
                timeoutSeconds);
        }

        public static bool TryReadOptionalPositiveInt(
            IReadOnlyList<string> args,
            string name,
            TextWriter error,
            out int? value)
        {
            value = null;
            string? rawValue = ReadOption(args, name);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return true;
            }

            if (!int.TryParse(
                    rawValue.Trim(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsedValue)
                || parsedValue <= 0)
            {
                error.WriteLine(name + " must be a positive integer.");
                return false;
            }

            value = parsedValue;
            return true;
        }

        public static bool TryNormalizeProbeFile(
            string localRoot,
            string probeFile,
            out string normalizedProbeFile,
            out string error)
        {
            normalizedProbeFile = string.Empty;
            error = string.Empty;
            if (Path.IsPathRooted(probeFile))
            {
                error = "--probe-file must be a relative path inside --local-root.";
                return false;
            }

            try
            {
                normalizedProbeFile = SyncPath.Normalize(probeFile);
            }
            catch (ArgumentException exception)
            {
                error = "--probe-file is invalid: " + exception.Message;
                return false;
            }

            string root = Path.GetFullPath(localRoot);
            string fullPath = Path.GetFullPath(Path.Combine(root, normalizedProbeFile.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal)
                && !string.Equals(fullPath, root, StringComparison.Ordinal))
            {
                error = "--probe-file must stay inside --local-root.";
                return false;
            }

            return true;
        }

        public static string? ReadOption(IReadOnlyList<string> args, string name)
        {
            for (int index = 0; index < args.Count - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        public static bool HasFlag(IReadOnlyList<string> args, string name)
        {
            return args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string? ReadPassword(IReadOnlyList<string> args)
        {
            string? password = ReadOption(args, "--password");
            if (!string.IsNullOrWhiteSpace(password))
            {
                return password;
            }

            string? passwordEnvironmentVariable = ReadOption(args, "--password-env");
            if (string.IsNullOrWhiteSpace(passwordEnvironmentVariable))
            {
                return null;
            }

            return Environment.GetEnvironmentVariable(passwordEnvironmentVariable.Trim());
        }

        private static string? NormalizeOptional(string? value)
        {
            string? trimmed = value?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }
}
