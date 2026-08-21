// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private class NoTransferRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public int TransferCalls { get; private set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not upload files.");
            }

            public Task DownloadFileAsync(
                Guid nodeFileId,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not download files.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not move remote files.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not delete remote files.");
            }
        }

        private class DelegateSyncPairWork : ISyncPairWork
        {
            private readonly Func<SyncPairSettings, SyncRunRequest, CancellationToken, Task> _run;

            public DelegateSyncPairWork(Func<SyncPairSettings, SyncRunRequest, CancellationToken, Task> run)
            {
                _run = run ?? throw new ArgumentNullException(nameof(run));
            }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return _run(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return _run(syncPair, request, cancellationToken);
            }
        }

        private class NoopSyncPairWork : ISyncPairWork
        {
            public static NoopSyncPairWork Instance { get; } = new();

            private NoopSyncPairWork()
            {
            }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FailOnInnerSyncPairWork : ISyncPairWork
        {
            private readonly string _message;

            public FailOnInnerSyncPairWork(string message)
            {
                _message = message;
            }

            public int RunCalls { get; private set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCalls++;
                throw new InvalidOperationException(_message);
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                RunCalls++;
                throw new InvalidOperationException(_message);
            }
        }
    }
}
