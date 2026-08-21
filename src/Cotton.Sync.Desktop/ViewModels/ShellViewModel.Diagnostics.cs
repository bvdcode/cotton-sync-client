// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private void RefreshDiagnosticsItems()
        {
            DiagnosticsItems.Clear();
            AddDiagnosticItem("App version", AppVersion);
            AddDiagnosticItem("Server", string.IsNullOrWhiteSpace(ServerUrl) ? "Not configured" : ServerUrl);
            AddDiagnosticItem("Account", AccountName);
            AddDiagnosticItem("Theme", ThemeModeLabel);
            AddDiagnosticItem("Windows virtual files", IsWindowsVirtualFilesSupported ? "Supported" : "Unavailable");
            AddDiagnosticItem("Windows virtual files details", WindowsVirtualFilesDetails);
            AddDiagnosticItem("Data folder", string.IsNullOrWhiteSpace(DataDirectory) ? "Unknown" : DataDirectory);
            AddDiagnosticItem("Preferences database", string.IsNullOrWhiteSpace(AppDatabasePath) ? "Unknown" : AppDatabasePath);
            AddDiagnosticItem("Sync state database", string.IsNullOrWhiteSpace(SyncStateDatabasePath) ? "Unknown" : SyncStateDatabasePath);
            AddDiagnosticItem("Token store", string.IsNullOrWhiteSpace(TokenStorePath) ? "Unknown" : TokenStorePath);
            AddDiagnosticItem("Sync pairs", SyncPairs.Count.ToString(CultureInfo.InvariantCulture));
            foreach (SyncPairRowViewModel syncPair in SyncPairs)
            {
                AddDiagnosticItem(syncPair.DisplayName + " id", syncPair.Id.ToString());
                AddDiagnosticItem(syncPair.DisplayName + " local", syncPair.LocalPath);
                AddDiagnosticItem(syncPair.DisplayName + " remote", syncPair.RemotePath);
                AddDiagnosticItem(
                    syncPair.DisplayName + " remote id",
                    syncPair.RemoteRootNodeId?.ToString() ?? "Unknown");
                AddDiagnosticItem(syncPair.DisplayName + " mode", syncPair.ModeLabel);
                AddDiagnosticItem(syncPair.DisplayName + " Cloud Files sync root", GetCloudFilesSyncRootDiagnostic(syncPair));
                AddDiagnosticItem(syncPair.DisplayName + " status", syncPair.Status);
                AddDiagnosticItem(syncPair.DisplayName + " last sync", FormatDiagnosticUtc(syncPair.LastSyncedAtUtc));
                AddDiagnosticItem(
                    syncPair.DisplayName + " cursor",
                    syncPair.ChangeCursor?.ToString(CultureInfo.InvariantCulture) ?? "0");
                AddDiagnosticItem(
                    syncPair.DisplayName + " last error",
                    string.IsNullOrWhiteSpace(syncPair.LastError) ? "None" : syncPair.LastError);
            }
        }

        private static string GetCloudFilesSyncRootDiagnostic(SyncPairRowViewModel syncPair)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return "Not used";
            }

            return syncPair.IsEnabled
                ? "Enabled; connects on sync startup"
                : "Disabled";
        }

        private void AddDiagnosticItem(string label, string value)
        {
            DiagnosticsItems.Add(new DiagnosticItemRowViewModel
            {
                Label = label,
                Value = value,
            });
        }

        private static string FormatDiagnosticUtc(DateTime? value)
        {
            return value is null
                ? "Never"
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture);
        }

        private static string GetRemoteParentPath(string remotePath)
        {
            string normalized = string.IsNullOrWhiteSpace(remotePath)
                ? "/"
                : "/" + remotePath.Replace('\\', '/').Trim('/');
            if (normalized == "/")
            {
                return "/";
            }

            int lastSlash = normalized.LastIndexOf('/');
            return lastSlash <= 0 ? "/" : normalized[..lastSlash];
        }

    }
}
