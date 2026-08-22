// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        private partial class FakeDesktopShellController : IDesktopShellController
        {

            public async Task<DesktopRemoteFolderListSnapshot> ListRemoteFoldersAsync(
                string remotePath,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ListRemoteFolderPaths.Add(remotePath);
                if (ListRemoteFoldersCompletion is not null)
                {
                    return await WaitForRemoteFoldersAsync(ListRemoteFoldersCompletion, cancellationToken)
                        .ConfigureAwait(false);
                }

                return RemoteFoldersByPath.GetValueOrDefault(remotePath, new DesktopRemoteFolderListSnapshot(remotePath, []));
            }


            private static async Task<DesktopRemoteFolderListSnapshot> WaitForRemoteFoldersAsync(
                TaskCompletionSource<DesktopRemoteFolderListSnapshot> completion,
                CancellationToken cancellationToken)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static state =>
                    {
                        TaskCompletionSource<DesktopRemoteFolderListSnapshot> taskCompletion = (TaskCompletionSource<DesktopRemoteFolderListSnapshot>)state!;
                        taskCompletion.TrySetCanceled();
                    },
                    completion);
                return await completion.Task.ConfigureAwait(false);
            }


            public Task<DesktopRemoteFolderSnapshot> CreateRemoteFolderAsync(
                string parentPath,
                string folderName,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreatedRemoteFolders.Add((parentPath, folderName));
                string path = parentPath == "/"
                    ? "/" + folderName.Trim()
                    : parentPath.TrimEnd('/') + "/" + folderName.Trim();
                DesktopRemoteFolderSnapshot folder = new DesktopRemoteFolderSnapshot(Guid.NewGuid(), folderName.Trim(), path);
                RemoteFoldersByPath[path] = new DesktopRemoteFolderListSnapshot(path, []);
                return Task.FromResult(folder);
            }


            public Task SignOutAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SignOutCalls++;
                return Task.CompletedTask;
            }


            public Task<SyncPairSettings> AddSyncPairAsync(
                DesktopSyncPairRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddedSyncPairRequest = request;
                Task<SyncPairSettings> addTask = AddSyncPairCompletion?.Task
                    ?? Task.FromResult(new SyncPairSettings
                    {
                        Id = Guid.NewGuid(),
                        DisplayName = Path.GetFileName(request.LocalFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        LocalRootPath = request.LocalFolderPath,
                        RemoteRootNodeId = Guid.NewGuid(),
                        RemoteDisplayPath = request.RemoteFolderPath,
                        IsEnabled = true,
                        Mode = request.Mode,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                    });
                return addTask.WaitAsync(cancellationToken);
            }


            public async Task SyncAllAsync(
                CancellationToken cancellationToken = default,
                Guid? syncPairId = null,
                RemoteDeletePlanApproval? approvedRemoteDeletePlan = null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncAllException is not null)
                {
                    throw SyncAllException;
                }

                SyncAllCalls++;
                LastSyncAllPairId = syncPairId;
                LastApprovedRemoteDeletePlan = approvedRemoteDeletePlan;
                if (SyncAllCompletion is not null)
                {
                    await SyncAllCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (SyncAllStatus is not null)
                {
                    ReportStatus(SyncAllStatus);
                }
            }


            public async Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PauseAllCalls++;
                if (PauseAllCompletion is not null)
                {
                    await PauseAllCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }


            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResumeAllCalls++;
                return Task.CompletedTask;
            }


            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpenedFolderPath = localPath;
                return Task.CompletedTask;
            }


            public Task OpenWebAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }


            public Task SetStartWithOperatingSystemAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }


            public Task SetNotificationsEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }


            public Task SetThemeModeAsync(AppThemeMode themeMode, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }


            public Task<DesktopSelfTestSnapshot> RunSelfTestAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(SelfTestSnapshot);
            }
        }
    }
}
