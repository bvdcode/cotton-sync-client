// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using System.Security.Principal;

namespace Cotton.Sync.WindowsShell
{
    internal static class Program
    {
        private const string ProviderId = "Cotton.Sync.Desktop";
        private const string ProviderAccount = "Default";

        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("Usage: Cotton.Sync.WindowsShell register <account> <root> <version> <icon-resource> | unregister <account>");
                    return 2;
                }

                return args[0] switch
                {
                    "register" when args.Length == 5 => await RegisterAsync(args[1], args[2], args[3], args[4]).ConfigureAwait(false),
                    "unregister" when args.Length == 2 => Unregister(args[1]),
                    "is-supported" => StorageProviderSyncRootManager.IsSupported() ? 0 : 1,
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
                DisplayNameResource = "Cotton Sync",
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

        private static int Unregister(string account)
        {
            string syncRootId = CreateSyncRootId(account);
            bool isRegistered = StorageProviderSyncRootManager
                .GetCurrentSyncRoots()
                .Any(root => string.Equals(root.Id, syncRootId, StringComparison.Ordinal));
            if (!isRegistered)
            {
                Console.WriteLine("not registered " + syncRootId);
                return 0;
            }

            StorageProviderSyncRootManager.Unregister(syncRootId);
            Console.WriteLine("unregistered " + syncRootId);
            return 0;
        }

        private static string CreateSyncRootId(string account)
        {
            string normalizedAccount = string.IsNullOrWhiteSpace(account)
                ? ProviderAccount
                : account.Trim();
            return ProviderId + "!" + WindowsIdentity.GetCurrent().User?.Value + "!" + normalizedAccount;
        }
    }
}
