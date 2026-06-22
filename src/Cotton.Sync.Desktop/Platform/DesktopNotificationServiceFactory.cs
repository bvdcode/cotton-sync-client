// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Text;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class DesktopNotificationServiceFactory
    {
        private const string NotifySendCommandName = "notify-send";
        private const string WindowsPowerShellCommandName = "powershell.exe";
        private const string PowerShellCoreCommandName = "pwsh.exe";
        private const int WindowsShortcutInspectionTimeoutMilliseconds = 5_000;
        private const string LinuxNotificationDetails =
            "requires DBus session bus; sender name, icon rendering, timeout, and actions depend on the desktop notification daemon; actions are not used";
        private const string WindowsNotificationDetails =
            "PowerShell is only the toast delivery helper; full installed notification identity requires a verified Start Menu AppUserModelID shortcut";

        public static IDesktopNotificationService CreateDefault()
        {
            return CreateForPlatform(
                ResolvePlatform(),
                Environment.GetEnvironmentVariable("PATH"),
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        }

        public static DesktopNotificationCapabilitySnapshot CreateCapabilitySnapshot()
        {
            return CreateCapabilitySnapshot(
                ResolvePlatform(),
                Environment.GetEnvironmentVariable("PATH"),
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        }

        public static DesktopNotificationCapabilitySnapshot CreateSelfTestCapabilitySnapshot()
        {
            return CreateCapabilitySnapshot(
                ResolvePlatform(),
                Environment.GetEnvironmentVariable("PATH"),
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
                VerifyWindowsStartMenuAppIdentity);
        }

        internal static IDesktopNotificationService CreateForPlatform(
            DesktopNotificationPlatform platform,
            string? pathValue,
            string? appBaseDirectory = null,
            string? dbusSessionBusAddress = null)
        {
            DesktopNotificationCapabilitySnapshot capabilities = CreateCapabilitySnapshot(
                platform,
                pathValue,
                appBaseDirectory,
                dbusSessionBusAddress);
            if (capabilities.Platform == DesktopNotificationPlatform.Linux)
            {
                return !capabilities.IsSupported || capabilities.ExecutablePath is null
                    ? new UnsupportedDesktopNotificationService()
                    : new NotifySendNotificationService(capabilities.ExecutablePath, capabilities.IconPath);
            }

            if (capabilities.Platform == DesktopNotificationPlatform.Windows)
            {
                return capabilities.ExecutablePath is null
                    ? new UnsupportedDesktopNotificationService()
                    : new WindowsToastNotificationService(capabilities.ExecutablePath, capabilities.IconPath);
            }

            return new UnsupportedDesktopNotificationService();
        }

        internal static DesktopNotificationCapabilitySnapshot CreateCapabilitySnapshot(
            DesktopNotificationPlatform platform,
            string? pathValue,
            string? appBaseDirectory = null,
            string? dbusSessionBusAddress = null,
            Func<string?, bool>? verifyWindowsInstalledIdentity = null)
        {
            string? iconPath = ResolveNotificationIconPath(appBaseDirectory ?? AppContext.BaseDirectory);
            if (platform == DesktopNotificationPlatform.Linux)
            {
                string? notifySendPath = ResolveExecutablePath(
                    NotifySendCommandName,
                    pathValue);
                bool hasSessionBus = !string.IsNullOrWhiteSpace(dbusSessionBusAddress);
                return new DesktopNotificationCapabilitySnapshot(
                    Platform: platform,
                    AdapterName: NotifySendCommandName,
                    IsSupported: notifySendPath is not null && hasSessionBus,
                    AppName: DesktopNotificationIdentity.AppName,
                    AppUserModelId: null,
                    ExecutablePath: notifySendPath,
                    IconPath: iconPath,
                    PlatformDetails: "session bus: " + (hasSessionBus ? "available" : "missing") + "; " + LinuxNotificationDetails);
            }

            if (platform == DesktopNotificationPlatform.Windows)
            {
                string? powerShellPath = ResolveFirstExecutablePath(
                    [WindowsPowerShellCommandName, PowerShellCoreCommandName],
                    pathValue);
                bool installedIdentityVerified =
                    powerShellPath is not null && verifyWindowsInstalledIdentity?.Invoke(powerShellPath) == true;
                return new DesktopNotificationCapabilitySnapshot(
                    Platform: platform,
                    AdapterName: "Windows toast",
                    IsSupported: powerShellPath is not null,
                    AppName: DesktopNotificationIdentity.AppName,
                    AppUserModelId: DesktopAppIdentity.AppUserModelId,
                    ExecutablePath: powerShellPath,
                    IconPath: iconPath,
                    PlatformDetails: WindowsNotificationDetails,
                    IsInstalledAppIdentityVerified: installedIdentityVerified,
                    InstalledAppIdentityDetails: "Start Menu AppUserModelID shortcut: "
                    + (installedIdentityVerified ? "verified" : "not verified"));
            }

            return new DesktopNotificationCapabilitySnapshot(
                Platform: DesktopNotificationPlatform.Unsupported,
                AdapterName: "Unsupported",
                IsSupported: false,
                AppName: DesktopNotificationIdentity.AppName,
                AppUserModelId: null,
                ExecutablePath: null,
                IconPath: iconPath);
        }

        internal static string? ResolveExecutablePath(string commandName, string? pathValue)
        {
            return ExecutablePathResolver.Resolve(commandName, pathValue);
        }

        internal static string? ResolveNotificationIconPath(string appBaseDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
            string candidate = Path.Combine(appBaseDirectory, "Assets", "icon-192.png");
            return File.Exists(candidate) ? candidate : null;
        }

        private static DesktopNotificationPlatform ResolvePlatform()
        {
            if (OperatingSystem.IsLinux())
            {
                return DesktopNotificationPlatform.Linux;
            }

            if (OperatingSystem.IsWindows())
            {
                return DesktopNotificationPlatform.Windows;
            }

            return DesktopNotificationPlatform.Unsupported;
        }

        private static string? ResolveFirstExecutablePath(
            IReadOnlyList<string> commandNames,
            string? pathValue)
        {
            foreach (string commandName in commandNames)
            {
                string? executablePath = ResolveExecutablePath(commandName, pathValue);
                if (executablePath is not null)
                {
                    return executablePath;
                }
            }

            return null;
        }

        private static bool VerifyWindowsStartMenuAppIdentity(string? powerShellPath)
        {
            if (string.IsNullOrWhiteSpace(powerShellPath))
            {
                return false;
            }

            foreach (string shortcutPath in EnumerateWindowsStartMenuShortcutCandidates())
            {
                if (!File.Exists(shortcutPath))
                {
                    continue;
                }

                if (TryReadShortcutAppUserModelId(powerShellPath, shortcutPath, out string? appUserModelId)
                    && string.Equals(appUserModelId, DesktopAppIdentity.AppUserModelId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateWindowsStartMenuShortcutCandidates()
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrWhiteSpace(programs))
            {
                yield return Path.Combine(programs, "Cotton Sync", "Cotton Sync.lnk");
            }

            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (!string.IsNullOrWhiteSpace(commonPrograms))
            {
                yield return Path.Combine(commonPrograms, "Cotton Sync", "Cotton Sync.lnk");
            }
        }

        private static bool TryReadShortcutAppUserModelId(
            string powerShellPath,
            string shortcutPath,
            out string? appUserModelId)
        {
            appUserModelId = null;
            try
            {
                using Process process = Process.Start(CreateShortcutInspectionStartInfo(powerShellPath, shortcutPath))
                    ?? throw new InvalidOperationException("PowerShell could not be started.");
                string output = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(WindowsShortcutInspectionTimeoutMilliseconds))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    return false;
                }

                if (process.ExitCode != 0)
                {
                    return false;
                }

                appUserModelId = output.Trim();
                return !string.IsNullOrWhiteSpace(appUserModelId);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }

        private static ProcessStartInfo CreateShortcutInspectionStartInfo(
            string powerShellPath,
            string shortcutPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(CreateShortcutInspectionCommand(shortcutPath))));
            return startInfo;
        }

        private static string CreateShortcutInspectionCommand(string shortcutPath)
        {
            string shortcutLiteral = ToPowerShellSingleQuotedLiteral(shortcutPath);
            return string.Join(
                Environment.NewLine,
                [
                    "$ErrorActionPreference = 'Stop'",
                    "$shortcut = " + shortcutLiteral,
                    "$folderPath = Split-Path -Parent -LiteralPath $shortcut",
                    "$shortcutFileName = Split-Path -Leaf -LiteralPath $shortcut",
                    "$shell = New-Object -ComObject Shell.Application",
                    "$folder = $shell.Namespace($folderPath)",
                    "if ($null -eq $folder) { exit 2 }",
                    "$shortcutItem = $folder.ParseName($shortcutFileName)",
                    "if ($null -eq $shortcutItem) { exit 3 }",
                    "$appUserModelId = [string]$shortcutItem.ExtendedProperty('System.AppUserModel.ID')",
                    "if ([string]::IsNullOrWhiteSpace($appUserModelId)) { exit 4 }",
                    "[Console]::Out.Write($appUserModelId)",
                ]);
        }

        private static string ToPowerShellSingleQuotedLiteral(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
    }
}
