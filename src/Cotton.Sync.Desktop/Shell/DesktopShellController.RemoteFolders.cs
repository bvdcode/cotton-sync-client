// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using AppRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        public async Task<DesktopRemoteFolderListSnapshot> ListRemoteFoldersAsync(
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost host = RequireHost();
            string normalizedPath = NormalizeRemotePath(remotePath);
            NodeDto current = await host.Nodes.ResolveAsync(
                normalizedPath == "/" ? null : normalizedPath,
                cancellationToken).ConfigureAwait(false);
            CottonPagedResult<NodeContentDto> pageResult = await host.Nodes.GetChildrenAsync(
                current.Id,
                page: 1,
                pageSize: 200,
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            NodeContentDto children = pageResult.Payload;
            List<DesktopRemoteFolderSnapshot> folders = children.Nodes
                .OrderBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(node => new DesktopRemoteFolderSnapshot(
                    node.Id,
                    node.Name,
                    CombineRemotePath(normalizedPath, node.Name)))
                .ToList();
            return new DesktopRemoteFolderListSnapshot(normalizedPath, folders);
        }

        public async Task<DesktopRemoteFolderSnapshot> CreateRemoteFolderAsync(
            string parentPath,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            DesktopSyncApplicationHost host = RequireHost();
            string normalizedPath = NormalizeRemotePath(parentPath);
            string normalizedName = NormalizeRequired(folderName, nameof(folderName));
            if (normalizedName.Contains('/') || normalizedName.Contains('\\'))
            {
                throw new ArgumentException("Cloud folder name cannot contain path separators.", nameof(folderName));
            }

            NodeDto parent = await host.Nodes.ResolveAsync(
                normalizedPath == "/" ? null : normalizedPath,
                cancellationToken).ConfigureAwait(false);
            NodeDto created = await host.Nodes.CreateAsync(parent.Id, normalizedName, cancellationToken)
                .ConfigureAwait(false);
            return new DesktopRemoteFolderSnapshot(
                created.Id,
                created.Name,
                CombineRemotePath(normalizedPath, created.Name));
        }

        private static string NormalizeRemotePath(string remotePath)
        {
            string normalized = NormalizeRequired(remotePath, nameof(remotePath)).Replace('\\', '/').Trim();
            normalized = normalized.Trim('/');
            return normalized.Length == 0 ? "/" : "/" + normalized;
        }

        private static string CombineRemotePath(string parentPath, string folderName)
        {
            string normalizedName = NormalizeRequired(folderName, nameof(folderName)).Trim('/');
            return parentPath == "/" ? "/" + normalizedName : parentPath + "/" + normalizedName;
        }

        private static string NormalizeRequired(string value, string parameterName)
        {
            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Value is required.", parameterName);
            }

            return normalized;
        }

        private static string? NormalizeOptional(string? value)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }
    }
}
