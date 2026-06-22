// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Composition
{
    internal class DesktopAppPaths
    {
        private const string CompanyDirectoryName = "Cotton";
        private const string ProductDirectoryName = "Sync";

        private DesktopAppPaths(string dataDirectory)
        {
            DataDirectory = dataDirectory;
            AppDatabasePath = Path.Combine(DataDirectory, "sync-app.db");
            SyncStateDatabasePath = Path.Combine(DataDirectory, "sync-state.db");
            TokenStorePath = Path.Combine(DataDirectory, "tokens.json");
            SingleInstanceLockPath = Path.Combine(DataDirectory, "cotton-sync.lock");
            LogFilePath = Path.Combine(DataDirectory, "cotton-sync.log");
            UpdateCacheDirectory = Path.Combine(DataDirectory, "updates");
        }

        public string DataDirectory { get; }

        public string AppDatabasePath { get; }

        public string SyncStateDatabasePath { get; }

        public string TokenStorePath { get; }

        public string SingleInstanceLockPath { get; }

        public string LogFilePath { get; }

        public string UpdateCacheDirectory { get; }

        public static DesktopAppPaths CreateDefault()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            return new DesktopAppPaths(Path.Combine(root, CompanyDirectoryName, ProductDirectoryName));
        }

        internal static DesktopAppPaths CreateForDataDirectory(string dataDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            return new DesktopAppPaths(dataDirectory);
        }
    }
}
