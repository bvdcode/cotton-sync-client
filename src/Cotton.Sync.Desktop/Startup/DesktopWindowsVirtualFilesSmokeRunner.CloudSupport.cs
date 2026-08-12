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
        private class RecordingCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                throw new NotSupportedException();
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                throw new NotSupportedException();
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
            {
                throw new NotSupportedException();
            }

            public void SetInSyncState(string filePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                throw new NotSupportedException();
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer);
            }

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }

            public void HydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }
        }

        private class StaticSmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private byte[] _content;

            public StaticSmokeContentProvider(byte[] content)
            {
                _content = content;
            }

            public int DownloadCount { get; private set; }

            public void SetContent(byte[] content)
            {
                ArgumentNullException.ThrowIfNull(content);
                _content = content;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                DownloadCount++;
                byte[] content = _content;
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    content.LongLength,
                    isCompleted: false));
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    content.LongLength,
                    content.LongLength,
                    isCompleted: true));
                destination.Position = 0;
            }
        }

        private class DictionarySmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly IReadOnlyDictionary<string, byte[]> _contentByPath;

            public DictionarySmokeContentProvider(IReadOnlyDictionary<string, byte[]> contentByPath)
            {
                _contentByPath = contentByPath ?? throw new ArgumentNullException(nameof(contentByPath));
            }

            public int DownloadCount { get; private set; }

            public List<string> DownloadedPaths { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                string normalizedPath = SyncPath.Normalize(identity.RelativePath);
                if (!_contentByPath.TryGetValue(normalizedPath, out byte[]? content))
                {
                    throw new InvalidOperationException("No smoke content was registered for the requested placeholder.");
                }

                DownloadCount++;
                DownloadedPaths.Add(normalizedPath);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    content.LongLength,
                    isCompleted: false));
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    content.LongLength,
                    content.LongLength,
                    isCompleted: true));
                destination.Position = 0;
            }
        }

        private class VfsShellShareLinkSmokeClient : IDesktopShellShareLinkClient
        {
            public ShellShareLinkTarget? LastTarget { get; private set; }

            public Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
                ShellShareLinkTarget target,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastTarget = target;
                string slug = target.Kind.ToString().ToLowerInvariant()
                    + "-"
                    + target.RelativePath.Replace('/', '-');
                return Task.FromResult(
                    DesktopShellShareLinkResult.Created(new Uri("https://share.example/s/" + Uri.EscapeDataString(slug))));
            }
        }

        private class VfsShellShareLinkSmokeClipboardService : IDesktopClipboardService
        {
            public string? CopiedText { get; private set; }

            public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopiedText = text;
                return Task.CompletedTask;
            }
        }

        private class VfsShellShareLinkSmokeNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => true;

            public string? LastMessage { get; private set; }

            public void Show(string title, string message)
            {
                LastMessage = message;
            }
        }

        private record SubstResult(int ExitCode, string Output, string Error);

        private record FileContentHash(long Length, string Sha256);
    }
}
