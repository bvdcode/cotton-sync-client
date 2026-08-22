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

            public Task<DesktopUpdateStatusSnapshot> CheckForUpdateAsync(
                DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Manual,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckForUpdateCalls++;
                CheckForUpdateSources.Add(source);
                if (UpdateCheckException is not null)
                {
                    throw UpdateCheckException;
                }

                return Task.FromResult(UpdateCheckSnapshot);
            }


            public Task<DesktopUpdateStatusSnapshot> DownloadUpdateAsync(
                DesktopUpdateCheckSource source = DesktopUpdateCheckSource.Download,
                IProgress<DesktopUpdateDownloadProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadUpdateCalls++;
                DownloadUpdateSources.Add(source);
                if (UpdateDownloadException is not null)
                {
                    throw UpdateDownloadException;
                }

                if (!SuppressDownloadProgress)
                {
                    DesktopUpdateDownloadProgress firstProgress = new DesktopUpdateDownloadProgress("0.0.2", "CottonSync-Windows-Setup.exe", 512, 1024);
                    DesktopUpdateDownloadProgress finalProgress = new DesktopUpdateDownloadProgress("0.0.2", "CottonSync-Windows-Setup.exe", 1024, 1024);
                    DownloadProgressReports.Add(firstProgress);
                    DownloadProgressReports.Add(finalProgress);
                    progress?.Report(firstProgress);
                    progress?.Report(finalProgress);
                }

                if (UpdateDownloadCompletion is not null)
                {
                    return UpdateDownloadCompletion.Task;
                }

                return Task.FromResult(UpdateDownloadSnapshot ?? UpdateCheckSnapshot);
            }


            public Task<DesktopUpdateInstallResult> InstallDownloadedUpdateAsync(string installerPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InstalledUpdatePath = installerPath;
                if (InstallUpdateException is not null)
                {
                    throw InstallUpdateException;
                }

                if (InstallUpdateCompletion is not null)
                {
                    return InstallUpdateCompletion.Task
                        .WaitAsync(cancellationToken)
                        .ContinueWith(
                            static task =>
                            {
                                task.GetAwaiter().GetResult();
                                return new DesktopUpdateInstallResult(42, false, null);
                            },
                            cancellationToken,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                }

                return Task.FromResult(new DesktopUpdateInstallResult(42, false, null));
            }


            public Task<string> ExportDiagnosticsAsync(CancellationToken cancellationToken = default)
            {
                return ExportDiagnosticsAsync(DesktopDiagnosticsExportOptions.Public, cancellationToken);
            }


            public Task<string> ExportDiagnosticsAsync(
                DesktopDiagnosticsExportOptions options,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExportDiagnosticsCalls++;
                ExportDiagnosticsStarted?.TrySetResult();
                if (ExportDiagnosticsException is not null)
                {
                    throw ExportDiagnosticsException;
                }

                if (ExportDiagnosticsCompletion is not null)
                {
                    return ExportDiagnosticsCompletion.Task.WaitAsync(cancellationToken);
                }

                return Task.FromResult(ExportDiagnosticsPath);
            }


            public void Dispose()
            {
            }


            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
