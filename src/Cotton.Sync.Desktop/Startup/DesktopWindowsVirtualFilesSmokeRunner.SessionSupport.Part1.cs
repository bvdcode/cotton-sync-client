// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk;
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
        private class SessionRestoreApplicationFactory : IDesktopSyncApplicationFactory
        {
            private readonly SyncApplicationService _app;
            private readonly Uri _expectedServerUrl;
            private readonly InMemoryAppStatusPublisher _statusPublisher;
            private readonly SessionRestoreMemoryTokenStore _tokenStore;

            public SessionRestoreApplicationFactory(
                SyncApplicationService app,
                SessionRestoreMemoryTokenStore tokenStore,
                InMemoryAppStatusPublisher statusPublisher,
                Uri expectedServerUrl)
            {
                _app = app ?? throw new ArgumentNullException(nameof(app));
                _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
                _statusPublisher = statusPublisher ?? throw new ArgumentNullException(nameof(statusPublisher));
                _expectedServerUrl = expectedServerUrl ?? throw new ArgumentNullException(nameof(expectedServerUrl));
            }

            public List<Uri> CreatedServerUrls { get; } = [];

            public DesktopSyncApplicationHost Create(Uri serverUrl)
            {
                CreatedServerUrls.Add(serverUrl);
                if (serverUrl != _expectedServerUrl)
                {
                    throw new InvalidOperationException("Unexpected Desktop session restore smoke server URL.");
                }

                return new DesktopSyncApplicationHost(
                    _app,
                    new SessionRestoreRemoteRootResolver(),
                    _statusPublisher,
                    new InMemoryAppActivityPublisher(),
                    new InMemorySessionRevocationPublisher(),
                    new InMemoryAppTransferProgressPublisher(),
                    new InMemoryAppRunProgressPublisher(),
                    _tokenStore,
                    new SessionRestoreNodeClient(),
                    new SessionRestoreSyncClient(),
                    new HttpClient(),
                    serverUrl);
            }
        }

        private class SessionRestoreMemoryTokenStore : ICottonTokenStore
        {
            private TokenPairDto? _tokens = new()
            {
                AccessToken = "session-restore-access",
                RefreshToken = "session-restore-refresh",
            };

            public Task<TokenPairDto?> GetAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_tokens is null ? null : Clone(_tokens));
            }

            public Task SaveAsync(TokenPairDto tokens, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(tokens);
                cancellationToken.ThrowIfCancellationRequested();
                _tokens = Clone(tokens);
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _tokens = null;
                return Task.CompletedTask;
            }

            private static TokenPairDto Clone(TokenPairDto tokens)
            {
                return new TokenPairDto
                {
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                };
            }
        }

        private class SessionRestoreRemoteRootResolver : IRemoteRootResolver
        {
            public Task<NodeDto> EnsureAsync(string? remotePath = null, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new NodeDto
                {
                    Id = Guid.Parse("25252525-2525-2525-2525-252525252525"),
                    LayoutId = Guid.Parse("26262626-2626-2626-2626-262626262626"),
                    ParentId = Guid.Parse("27272727-2727-2727-2727-272727272727"),
                    Name = string.IsNullOrWhiteSpace(remotePath)
                        ? "Cloud"
                        : remotePath.Trim('/'),
                });
            }
        }

        private class SessionRestoreNodeClient : ICottonNodeClient
        {
            public Task<NodeDto> ResolveAsync(string? path = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> GetAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<CottonPagedResult<NodeContentDto>> GetChildrenAsync(
                Guid nodeId,
                int page = 1,
                int pageSize = 100,
                int depth = 0,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> CreateAsync(Guid parentId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> MoveAsync(Guid nodeId, Guid parentId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> RenameAsync(Guid nodeId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> UpdateMetadataAsync(
                Guid nodeId,
                IReadOnlyDictionary<string, string> metadata,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<RestoreOutcomeDto> RestoreAsync(
                Guid nodeId,
                RestoreItemRequestDto? request = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<List<NodeDto>> GetAncestorsAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private class SessionRestoreSyncClient : ICottonSyncClient
        {
            public Task<SyncChangesResponseDto> GetChangesAsync(
                long sinceCursor = 0,
                int limit = 500,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new SyncChangesResponseDto
                {
                    SinceCursor = sinceCursor,
                    NextCursor = sinceCursor,
                    HasMore = false,
                });
            }
        }

        private class SmokeAutostartService : IAutostartService
        {
            public bool IsSupported => true;

            public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            }

            public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        private class NoopSyncPairPrerequisiteValidator : ISyncPairPrerequisiteValidator
        {
            public static NoopSyncPairPrerequisiteValidator Instance { get; } = new();

            public Task<IReadOnlyList<SyncPairValidationError>> ValidateAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncPairValidationError>>([]);
            }
        }

        private class NoopAppPreferencesStore : IAppPreferencesStore
        {
            private AppPreferences _preferences = new();

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_preferences);
            }

            public Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
            {
                _preferences = preferences;
                return Task.CompletedTask;
            }
        }

        private class NoopAuthFlow : IAuthFlow
        {
            public static NoopAuthFlow Instance { get; } = new();

            public Task<AuthSession> SignInAsync(
                PasswordSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(CreateSession());
            }

            public Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(CreateSession());
            }

            public Task SignOutAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            private static AuthSession CreateSession()
            {
                return new AuthSession(
                    Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    "smoke",
                    null,
                    false);
            }
        }

        private class NoopAppCodeBrowserAuthFlow : IAppCodeBrowserAuthFlow
        {
            public static NoopAppCodeBrowserAuthFlow Instance { get; } = new();

            public Task<AuthSession> SignInAsync(
                AppCodeBrowserSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AuthSession(
                    Guid.Parse("99999999-9999-9999-9999-999999999999"),
                    "browser-smoke",
                    null,
                    false));
            }
        }

        private class NoopPlatformCommandService : IPlatformCommandService
        {
            public static NoopPlatformCommandService Instance { get; } = new();

            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
