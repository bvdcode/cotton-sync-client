// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsStorageProviderSyncRootRegistrar : IWindowsStorageProviderSyncRootRegistrar
    {
        private const int CommandTimeoutMilliseconds = 30_000;
        private const string HelperRelativePath = "WindowsShell";
        private const string HelperExecutableName = "Cotton.Sync.WindowsShell.exe";

        private readonly string _helperPath;

        public WindowsStorageProviderSyncRootRegistrar(string helperPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
            _helperPath = helperPath;
        }

        public static WindowsStorageProviderSyncRootRegistrar? TryCreateDefault()
        {
            string helperPath = Path.Combine(AppContext.BaseDirectory, HelperRelativePath, HelperExecutableName);
            return File.Exists(helperPath)
                ? new WindowsStorageProviderSyncRootRegistrar(helperPath)
                : null;
        }

        public static string ResolveDefaultIconResource()
        {
            return Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe");
        }

        public bool IsSupported()
        {
            return File.Exists(_helperPath) && Run("is-supported").ExitCode == 0;
        }

        public void Register(WindowsStorageProviderSyncRootRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            WindowsStorageProviderSyncRootCommandResult result = Run(
                "register",
                ToAccount(registration.SyncPairId),
                registration.LocalRootPath,
                registration.ProviderVersion,
                registration.IconResource);
            ThrowIfFailed(result, "register");
        }

        public void Unregister(Guid syncPairId)
        {
            WindowsStorageProviderSyncRootCommandResult result = Run("unregister", ToAccount(syncPairId));
            ThrowIfFailed(result, "unregister");
        }

        internal static ProcessStartInfo CreateStartInfo(string helperPath, IReadOnlyList<string> arguments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
            ArgumentNullException.ThrowIfNull(arguments);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        private WindowsStorageProviderSyncRootCommandResult Run(params string[] arguments)
        {
            ProcessStartInfo startInfo = CreateStartInfo(_helperPath, arguments);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows shell helper could not be started.");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException("Windows shell helper timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            return new WindowsStorageProviderSyncRootCommandResult(process.ExitCode, output, error);
        }

        private static void ThrowIfFailed(WindowsStorageProviderSyncRootCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "Windows StorageProvider sync root "
                + operation
                + " failed: "
                + CleanSingleLine(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error));
        }

        private static string ToAccount(Guid syncPairId)
        {
            return syncPairId.ToString("N");
        }

        private static string CleanSingleLine(string value)
        {
            return (string.IsNullOrWhiteSpace(value) ? "Operation could not be completed." : value)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private readonly record struct WindowsStorageProviderSyncRootCommandResult(
            int ExitCode,
            string Output,
            string Error);
    }
}
