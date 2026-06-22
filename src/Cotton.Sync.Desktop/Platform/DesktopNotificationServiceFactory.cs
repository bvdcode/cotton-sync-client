// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.CSharp.RuntimeBinder;

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

                if (TryReadShortcutAppUserModelId(shortcutPath, out string? appUserModelId)
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

        internal static bool TryReadShortcutAppUserModelId(
            string shortcutPath,
            out string? appUserModelId)
        {
            appUserModelId = null;
            if (!OperatingSystem.IsWindows() || !File.Exists(shortcutPath))
            {
                return false;
            }

            TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread inspectionThread = new(() =>
            {
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        completion.TrySetResult(null);
                        return;
                    }

                    completion.TrySetResult(ReadShortcutAppUserModelId(shortcutPath));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            inspectionThread.SetApartmentState(ApartmentState.STA);
            inspectionThread.IsBackground = true;
            inspectionThread.Start();

            try
            {
                if (!completion.Task.Wait(WindowsShortcutInspectionTimeoutMilliseconds))
                {
                    return false;
                }

                appUserModelId = completion.Task.GetAwaiter().GetResult();
                return !string.IsNullOrWhiteSpace(appUserModelId);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidOperationException
                or ObjectDisposedException
                or COMException
                or RuntimeBinderException)
            {
                return false;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string? ReadShortcutAppUserModelId(string shortcutPath)
        {
            string fullShortcutPath = Path.GetFullPath(shortcutPath);
            string? folderPath = Path.GetDirectoryName(fullShortcutPath);
            string shortcutFileName = Path.GetFileName(fullShortcutPath);
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(shortcutFileName))
            {
                return null;
            }

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Shell.Application COM object could not be created.");
            dynamic folder = shell.Namespace(folderPath);
            if (folder is null)
            {
                return null;
            }

            dynamic shortcutItem = folder.ParseName(shortcutFileName);
            if (shortcutItem is null)
            {
                return null;
            }

            string appUserModelId = (string)shortcutItem.ExtendedProperty("System.AppUserModel.ID");
            return string.IsNullOrWhiteSpace(appUserModelId) ? null : appUserModelId.Trim();
        }
    }
}
