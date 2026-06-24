// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Microsoft.Win32;
using System.Security.Principal;

namespace Cotton.Sync.WindowsShell
{
    internal static class Program
    {
        private const string ProviderId = "Cotton.Sync.Desktop";
        private const string ProviderAccount = "Default";
        private const string ProviderDisplayName = "Cotton Cloud";
        private const string LegacyProviderDisplayName = "Cotton Sync";
        private const string ShellNamespaceRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
        private const string ClassesClsidRegistryPath = @"Software\Classes\CLSID";
        private const string WowClassesClsidRegistryPath = @"Software\Classes\WOW6432Node\CLSID";
        private const string PinnedToNamespaceTreeValueName = "System.IsPinnedToNameSpaceTree";

        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("Usage: Cotton.Sync.WindowsShell register <account> <root> <version> <icon-resource> | unregister <account> | unregister-all | is-supported | is-registered <account>");
                    return 2;
                }

                return args[0] switch
                {
                    "register" when args.Length == 5 => await RegisterAsync(args[1], args[2], args[3], args[4]).ConfigureAwait(false),
                    "unregister" when args.Length == 2 => Unregister(args[1]),
                    "unregister-all" when args.Length == 1 => UnregisterAll(),
                    "is-supported" => StorageProviderSyncRootManager.IsSupported() ? 0 : 1,
                    "is-registered" when args.Length == 2 => IsRegistered(args[1]),
                    _ => 2,
                };
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private static async Task<int> RegisterAsync(
            string account,
            string rootPath,
            string version,
            string iconResource)
        {
            if (!StorageProviderSyncRootManager.IsSupported())
            {
                Console.Error.WriteLine("StorageProvider sync root registration is not supported on this Windows build.");
                return 1;
            }

            string fullRootPath = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(fullRootPath);
            StorageFolder rootFolder = await StorageFolder.GetFolderFromPathAsync(fullRootPath).AsTask().ConfigureAwait(false);
            var syncRootInfo = new StorageProviderSyncRootInfo
            {
                Id = CreateSyncRootId(account),
                Path = rootFolder,
                DisplayNameResource = ProviderDisplayName,
                IconResource = iconResource,
                HydrationPolicy = StorageProviderHydrationPolicy.Progressive,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                    | StorageProviderInSyncPolicy.DirectoryCreationTime
                    | StorageProviderInSyncPolicy.FileLastWriteTime
                    | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                Version = version,
                ShowSiblingsAsGroup = false,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                AllowPinning = true,
                Context = CryptographicBuffer.ConvertStringToBinary(
                    ProviderId + "|" + fullRootPath,
                    BinaryStringEncoding.Utf8),
            };

            StorageProviderSyncRootManager.Register(syncRootInfo);
            Console.WriteLine("registered " + syncRootInfo.Id + " -> " + fullRootPath);
            return 0;
        }

        private static int UnregisterAll()
        {
            string prefix = CreateSyncRootIdPrefix();
            int unregistered = 0;
            int failures = 0;
            if (StorageProviderSyncRootManager.IsSupported())
            {
                foreach (StorageProviderSyncRootInfo syncRoot in StorageProviderSyncRootManager
                    .GetCurrentSyncRoots()
                    .Where(root => root.Id.StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray())
                {
                    try
                    {
                        StorageProviderSyncRootManager.Unregister(syncRoot.Id);
                        unregistered++;
                        Console.WriteLine("unregistered " + syncRoot.Id);
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        Console.Error.WriteLine("failed " + syncRoot.Id + ": " + exception.Message);
                    }
                }
            }

            int shellRootsRemoved = RemoveOrphanedShellNamespaceRoots(prefix);
            int legacyClassIdsRemoved = RemoveLegacyPinnedProviderClassIds();
            Console.WriteLine(
                "unregister-all storage-provider="
                + unregistered.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " shell-namespace="
                + shellRootsRemoved.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " legacy-clsid="
                + legacyClassIdsRemoved.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " failures="
                + failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return failures == 0 ? 0 : 1;
        }

        private static int Unregister(string account)
        {
            string syncRootId = CreateSyncRootId(account);
            StorageProviderSyncRootInfo? syncRoot = StorageProviderSyncRootManager.IsSupported()
                ? StorageProviderSyncRootManager
                .GetCurrentSyncRoots()
                .FirstOrDefault(root => string.Equals(root.Id, syncRootId, StringComparison.Ordinal))
                : null;
            string? targetFolderPath = syncRoot?.Path.Path;

            if (syncRoot is not null)
            {
                StorageProviderSyncRootManager.Unregister(syncRootId);
                Console.WriteLine("unregistered " + syncRootId);
            }
            else
            {
                Console.WriteLine("not registered " + syncRootId);
            }

            int shellRootsRemoved = RemoveOrphanedShellNamespaceRoot(syncRootId);
            int classIdsRemoved = string.IsNullOrWhiteSpace(targetFolderPath)
                ? 0
                : RemoveClassIdSubKeysForTargetFolderPath(targetFolderPath);
            Console.WriteLine(
                "unregister shell-namespace="
                + shellRootsRemoved.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " class-id="
                + classIdsRemoved.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }

        private static int IsRegistered(string account)
        {
            string syncRootId = CreateSyncRootId(account);
            bool isRegistered = StorageProviderSyncRootManager.IsSupported()
                && StorageProviderSyncRootManager
                    .GetCurrentSyncRoots()
                    .Any(root => string.Equals(root.Id, syncRootId, StringComparison.Ordinal));
            Console.WriteLine((isRegistered ? "registered " : "not registered ") + syncRootId);
            return isRegistered ? 0 : 3;
        }

        private static int RemoveOrphanedShellNamespaceRoots(string syncRootIdPrefix)
        {
            return RemoveShellNamespaceRoots(
                syncRootId => syncRootId.StartsWith(syncRootIdPrefix, StringComparison.Ordinal));
        }

        private static int RemoveOrphanedShellNamespaceRoot(string syncRootId)
        {
            return RemoveShellNamespaceRoots(
                currentSyncRootId => string.Equals(currentSyncRootId, syncRootId, StringComparison.Ordinal));
        }

        private static int RemoveShellNamespaceRoots(Func<string, bool> matchesSyncRootId)
        {
            using RegistryKey? namespaceKey = Registry.CurrentUser.OpenSubKey(ShellNamespaceRegistryPath, writable: true);
            if (namespaceKey is null)
            {
                return 0;
            }

            int removed = 0;
            foreach (string subKeyName in namespaceKey.GetSubKeyNames())
            {
                using RegistryKey? syncRootKey = namespaceKey.OpenSubKey(subKeyName);
                if (syncRootKey?.GetValue(null) is not string syncRootId
                    || !matchesSyncRootId(syncRootId))
                {
                    continue;
                }

                namespaceKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                DeleteClassIdSubKey(ClassesClsidRegistryPath, subKeyName);
                DeleteClassIdSubKey(WowClassesClsidRegistryPath, subKeyName);
                removed++;
                Console.WriteLine("removed shell namespace " + subKeyName + " -> " + syncRootId);
            }

            return removed;
        }

        private static int RemoveLegacyPinnedProviderClassIds()
        {
            return RemoveLegacyPinnedProviderClassIds(ClassesClsidRegistryPath)
                + RemoveLegacyPinnedProviderClassIds(WowClassesClsidRegistryPath);
        }

        private static int RemoveClassIdSubKeysForTargetFolderPath(string targetFolderPath)
        {
            string normalizedTargetFolderPath = NormalizePathForComparison(targetFolderPath);
            return RemoveClassIdSubKeysForTargetFolderPath(ClassesClsidRegistryPath, normalizedTargetFolderPath)
                + RemoveClassIdSubKeysForTargetFolderPath(WowClassesClsidRegistryPath, normalizedTargetFolderPath);
        }

        private static int RemoveClassIdSubKeysForTargetFolderPath(
            string registryPath,
            string normalizedTargetFolderPath)
        {
            using RegistryKey? parentKey = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            if (parentKey is null)
            {
                return 0;
            }

            int removed = 0;
            foreach (string subKeyName in parentKey.GetSubKeyNames())
            {
                using RegistryKey? initPropertyBagKey = parentKey.OpenSubKey(subKeyName + @"\Instance\InitPropertyBag");
                if (initPropertyBagKey?.GetValue("TargetFolderPath") is not string targetFolderPath
                    || !string.Equals(
                        NormalizePathForComparison(targetFolderPath),
                        normalizedTargetFolderPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                removed++;
                Console.WriteLine("removed shell class " + subKeyName + " -> " + targetFolderPath);
            }

            return removed;
        }

        private static int RemoveLegacyPinnedProviderClassIds(string registryPath)
        {
            using RegistryKey? parentKey = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            if (parentKey is null)
            {
                return 0;
            }

            int removed = 0;
            foreach (string subKeyName in parentKey.GetSubKeyNames())
            {
                using RegistryKey? classIdKey = parentKey.OpenSubKey(subKeyName);
                if (classIdKey?.GetValue(null) is not string displayName
                    || !IsCottonProviderDisplayName(displayName)
                    || !IsPinnedToNamespaceTree(classIdKey))
                {
                    continue;
                }

                parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                removed++;
                Console.WriteLine("removed legacy shell class " + subKeyName + " -> " + displayName);
            }

            return removed;
        }

        private static bool IsCottonProviderDisplayName(string displayName)
        {
            return string.Equals(displayName, ProviderDisplayName, StringComparison.Ordinal)
                || string.Equals(displayName, LegacyProviderDisplayName, StringComparison.Ordinal);
        }

        private static bool IsPinnedToNamespaceTree(RegistryKey classIdKey)
        {
            return classIdKey.GetValue(PinnedToNamespaceTreeValueName) switch
            {
                int intValue => intValue != 0,
                string stringValue => stringValue == "1",
                _ => false,
            };
        }

        private static void DeleteClassIdSubKey(string registryPath, string classId)
        {
            using RegistryKey? parentKey = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            parentKey?.DeleteSubKeyTree(classId, throwOnMissingSubKey: false);
        }

        private static string NormalizePathForComparison(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string CreateSyncRootId(string account)
        {
            return CreateSyncRootIdPrefix() + NormalizeAccount(account);
        }

        private static string CreateSyncRootIdPrefix()
        {
            return ProviderId + "!" + WindowsIdentity.GetCurrent().User?.Value + "!";
        }

        private static string NormalizeAccount(string account)
        {
            string normalizedAccount = string.IsNullOrWhiteSpace(account)
                ? ProviderAccount
                : account.Trim();
            return normalizedAccount;
        }
    }
}
