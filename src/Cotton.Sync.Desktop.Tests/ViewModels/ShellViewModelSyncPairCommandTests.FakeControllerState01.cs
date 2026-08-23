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
            private readonly DesktopShellSnapshot _snapshot;


            public FakeDesktopShellController(DesktopShellSnapshot snapshot)
            {
                _snapshot = snapshot;
            }


            public event EventHandler<DesktopSyncStatusSnapshot>? StatusChanged;


            public event EventHandler<DesktopActivitySnapshot>? ActivityReported;


            public event EventHandler<DesktopSessionRevocationSnapshot>? SessionRevoked;


            public event EventHandler<DesktopTransferProgressSnapshot>? TransferProgressChanged;


            public event EventHandler<DesktopRunProgressSnapshot>? RunProgressChanged;


            public Guid? EnabledSyncPairId { get; private set; }


            public bool? EnabledSyncPairValue { get; private set; }


            public Guid? RemovedSyncPairId { get; private set; }


            public Guid? RenamedSyncPairId { get; private set; }


            public string? RenamedSyncPairDisplayName { get; private set; }


            public DesktopSyncPairRequest? AddedSyncPairRequest { get; private set; }


            public int SignOutCalls { get; private set; }


            public DesktopSelfTestSnapshot SelfTestSnapshot { get; set; } = new([]);


            public DesktopUpdateStatusSnapshot UpdateCheckSnapshot { get; set; } = new(
                DesktopAppVersion.Current,
                DesktopAppVersion.Current,
                false,
                false,
                "Cotton Sync is up to date.",
                null,
                null);


            public DesktopUpdateStatusSnapshot? UpdateDownloadSnapshot { get; set; }


            public TaskCompletionSource<DesktopUpdateStatusSnapshot>? UpdateDownloadCompletion { get; set; }


            public TaskCompletionSource? InstallUpdateCompletion { get; set; }


            public bool SuppressDownloadProgress { get; set; }


            public Exception? UpdateCheckException { get; set; }


            public Exception? UpdateDownloadException { get; set; }


            public Exception? InstallUpdateException { get; set; }


            public int CheckForUpdateCalls { get; private set; }


            public int DownloadUpdateCalls { get; private set; }


            public List<DesktopUpdateCheckSource> CheckForUpdateSources { get; } = [];


            public List<DesktopUpdateCheckSource> DownloadUpdateSources { get; } = [];


            public List<DesktopUpdateDownloadProgress> DownloadProgressReports { get; } = [];


            public string? InstalledUpdatePath { get; private set; }


            public DesktopServerProbeResult? ServerProbeResult { get; set; }


            public bool IgnoreServerProbeCancellation { get; set; }


            public Dictionary<string, DesktopServerProbeResult> ServerProbeResultsByUrl { get; } = [];


            public Dictionary<string, Queue<Exception>> ServerProbeExceptionsByUrl { get; } = [];


            public Dictionary<string, TaskCompletionSource<DesktopServerProbeResult>> ServerProbeCompletionsByUrl { get; } = [];


            public DesktopSignInRequest? SignInRequest { get; private set; }


            public string? BrowserSignInServerUrl { get; private set; }


            public TaskCompletionSource<AuthSession>? BrowserSignInCompletion { get; set; }


            public Exception? LoadException { get; set; }


            public TaskCompletionSource<bool>? LoadCompletion { get; set; }


            public bool LoadStarted { get; private set; }


            public Dictionary<string, DesktopRemoteFolderListSnapshot> RemoteFoldersByPath { get; } = [];


            public TaskCompletionSource<DesktopRemoteFolderListSnapshot>? ListRemoteFoldersCompletion { get; set; }


            public List<string> ListRemoteFolderPaths { get; } = [];


            public List<(string ParentPath, string FolderName)> CreatedRemoteFolders { get; } = [];


            public List<string> ProbedServerUrls { get; } = [];


            public int SyncAllCalls { get; private set; }


            public Guid? LastSyncAllPairId { get; private set; }


            public RemoteDeletePlanApproval? LastApprovedRemoteDeletePlan { get; private set; }


            public int PauseAllCalls { get; private set; }


            public int ResumeAllCalls { get; private set; }


            public Exception? SyncAllException { get; set; }


            public TaskCompletionSource<bool>? SyncAllCompletion { get; set; }


            public DesktopSyncStatusSnapshot? SyncAllStatus { get; set; }


            public TaskCompletionSource<bool>? PauseAllCompletion { get; set; }

            public TaskCompletionSource<bool>? ResumeAllCompletion { get; set; }


            public int ExportDiagnosticsCalls { get; private set; }


            public string ExportDiagnosticsPath { get; set; } = "/tmp/cotton-sync-diagnostics.zip";


            public Exception? ExportDiagnosticsException { get; set; }


            public TaskCompletionSource? ExportDiagnosticsStarted { get; set; }


            public TaskCompletionSource<string>? ExportDiagnosticsCompletion { get; set; }


            public TaskCompletionSource? RemoveSyncPairStarted { get; set; }


            public TaskCompletionSource? RemoveSyncPairCompletion { get; set; }


            public int? RemoveSyncPairThreadId { get; private set; }


            public TaskCompletionSource<SyncPairSettings>? AddSyncPairCompletion { get; set; }


            public string? OpenedFolderPath { get; private set; }


            public Exception? SignInException { get; set; }


            public DesktopStoredSessionRestoreSnapshot StoredSessionRestoreSnapshot { get; set; } =
                new(null, false, null);


            public TaskCompletionSource<DesktopStoredSessionRestoreSnapshot>? StoredSessionRestoreCompletion { get; set; }


            public int RestoreStoredSessionCalls { get; private set; }


            public string? RestoredSessionServerUrl { get; private set; }
        }
    }
}
