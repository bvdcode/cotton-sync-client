// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
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
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        private class FakeCottonTokenStore : ICottonTokenStore
        {
            private TokenPairDto? _tokens;

            public FakeCottonTokenStore(bool hasStoredTokens = true)
            {
                _tokens = hasStoredTokens
                    ? new TokenPairDto
                    {
                        AccessToken = "access-token",
                        RefreshToken = "refresh-token",
                    }
                    : null;
            }

            public int SaveAsyncCalls { get; private set; }

            public TokenPairDto? LastSavedTokens { get; private set; }

            public int ClearAsyncCalls { get; private set; }

            public Task<TokenPairDto?> GetAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_tokens is null ? null : Clone(_tokens));
            }

            public Task SaveAsync(TokenPairDto tokens, CancellationToken cancellationToken = default)
            {
                SaveAsyncCalls++;
                _tokens = Clone(tokens);
                LastSavedTokens = Clone(tokens);
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                ClearAsyncCalls++;
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

        private class FakeRemoteRootResolver : IRemoteRootResolver
        {
            public Task<NodeDto> EnsureAsync(string? remotePath = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new NodeDto
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    LayoutId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ParentId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Name = string.IsNullOrWhiteSpace(remotePath)
                        ? "Cloud"
                        : remotePath.Trim('/'),
                });
            }
        }

        private class FakeCottonNodeClient : ICottonNodeClient
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

        private class FakeCottonSyncClient : ICottonSyncClient
        {
            public Task<SyncChangesResponseDto> GetChangesAsync(
                long sinceCursor = 0,
                int limit = 500,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangesResponseDto
                {
                    SinceCursor = sinceCursor,
                    NextCursor = sinceCursor,
                    HasMore = false,
                });
            }
        }
    }
}
