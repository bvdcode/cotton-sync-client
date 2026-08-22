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
        private class FakeDesktopApplicationHost
        {
            private FakeDesktopApplicationHost(Uri serverUrl, FakeCottonTokenStore? tokenStore)
            {
                TokenStore = tokenStore ?? new FakeCottonTokenStore();
                App = new FakeSyncApplicationService(TokenStore);
                AsyncResource = new FakeAsyncResource();
                StatusPublisher = new InMemoryAppStatusPublisher();
                SessionRevocationPublisher = new InMemorySessionRevocationPublisher();
                TransferProgressPublisher = new InMemoryAppTransferProgressPublisher();
                RunProgressPublisher = new InMemoryAppRunProgressPublisher();
                Host = new DesktopSyncApplicationHost(
                    App,
                    new FakeRemoteRootResolver(),
                    StatusPublisher,
                    new InMemoryAppActivityPublisher(),
                    SessionRevocationPublisher,
                    TransferProgressPublisher,
                    RunProgressPublisher,
                    TokenStore,
                    new FakeCottonNodeClient(),
                    new FakeCottonSyncClient(),
                    new HttpClient(),
                    serverUrl,
                    AsyncResource);
            }

            public FakeSyncApplicationService App { get; }

            public InMemoryAppStatusPublisher StatusPublisher { get; }

            public InMemorySessionRevocationPublisher SessionRevocationPublisher { get; }

            public InMemoryAppTransferProgressPublisher TransferProgressPublisher { get; }

            public InMemoryAppRunProgressPublisher RunProgressPublisher { get; }

            public FakeAsyncResource AsyncResource { get; }

            public FakeCottonTokenStore TokenStore { get; }

            public DesktopSyncApplicationHost Host { get; }

            public static FakeDesktopApplicationHost Create(Uri serverUrl, FakeCottonTokenStore? tokenStore = null)
            {
                return new FakeDesktopApplicationHost(serverUrl, tokenStore);
            }
        }

        private class FakeAsyncResource : IAsyncDisposable
        {
            public int DisposeAsyncCalls { get; private set; }

            public Exception? DisposeException { get; set; }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCalls++;
                if (DisposeException is not null)
                {
                    throw DisposeException;
                }

                return ValueTask.CompletedTask;
            }
        }

        private class FakeSyncApplicationService : ISyncApplicationService
        {
            private readonly ICottonTokenStore _tokenStore;

            public FakeSyncApplicationService(ICottonTokenStore tokenStore)
            {
                _tokenStore = tokenStore;
            }

            public int RestoreSessionCalls { get; private set; }

            public int StopSyncCalls { get; private set; }

            public int StartSyncCalls { get; private set; }

            public int SaveSyncPairCalls { get; private set; }

            public int DeleteSyncPairCalls { get; private set; }

            public int SyncNowCalls { get; private set; }

            public SyncRunRequest? LastSyncNowRequest { get; private set; }

            public Guid? LastSyncNowPairId { get; private set; }

            public SyncPairSettings? SavedSyncPair { get; private set; }

            public Guid? DeletedSyncPairId { get; private set; }

            public Exception? SyncNowException { get; set; }

            public Exception? RestoreSessionException { get; set; }

            public Queue<Exception> RestoreSessionExceptions { get; } = [];

            public IAppPreferencesStore? PreferencesStore { get; set; }

            public ISyncPairSettingsStore? SyncPairStore { get; set; }

            public TaskCompletionSource? StartSyncStarted { get; set; }

            public TaskCompletionSource? StartSyncRelease { get; set; }

            public TaskCompletionSource? SyncNowStarted { get; set; }

            public TaskCompletionSource? SyncNowRelease { get; set; }

            public async Task<AuthSession> SignInAsync(
                PasswordSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                await _tokenStore.SaveAsync(CreateTokenPair(request.Username), cancellationToken);
                return CreateSession(request.Username);
            }

            public async Task<AuthSession> SignInWithBrowserAsync(
                AppCodeBrowserSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                string username = request.DeviceName ?? "browser";
                await _tokenStore.SaveAsync(CreateTokenPair(username), cancellationToken);
                return CreateSession(username);
            }

            public Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
            {
                RestoreSessionCalls++;
                if (RestoreSessionExceptions.TryDequeue(out Exception? queuedException))
                {
                    throw queuedException;
                }

                if (RestoreSessionException is not null)
                {
                    throw RestoreSessionException;
                }

                return Task.FromResult(CreateSession("restored"));
            }

            public Task SignOutAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<AppPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AppPreferences());
            }

            public async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
            {
                if (PreferencesStore is null)
                {
                    return;
                }

                await PreferencesStore.InitializeAsync(cancellationToken);
                await PreferencesStore.SaveAsync(preferences, cancellationToken);
            }

            public async Task<IReadOnlyList<SyncPairSettings>> ListSyncPairsAsync(CancellationToken cancellationToken = default)
            {
                if (SyncPairStore is null)
                {
                    return [];
                }

                await SyncPairStore.InitializeAsync(cancellationToken);
                return await SyncPairStore.ListAsync(cancellationToken);
            }

            public async Task<SyncPairSettings?> GetSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                if (SyncPairStore is null)
                {
                    return null;
                }

                await SyncPairStore.InitializeAsync(cancellationToken);
                return await SyncPairStore.GetAsync(syncPairId, cancellationToken);
            }

            public async Task<SyncPairSaveResult> SaveSyncPairAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                SaveSyncPairCalls++;
                SavedSyncPair = syncPair;
                if (SyncPairStore is not null)
                {
                    await SyncPairStore.InitializeAsync(cancellationToken);
                    await SyncPairStore.UpsertAsync(syncPair, cancellationToken);
                }

                return SyncPairSaveResult.Saved(new SyncPairValidationResult([]));
            }

            public async Task DeleteSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                DeleteSyncPairCalls++;
                DeletedSyncPairId = syncPairId;
                if (SyncPairStore is not null)
                {
                    await SyncPairStore.InitializeAsync(cancellationToken);
                    await SyncPairStore.DeleteAsync(syncPairId, cancellationToken);
                }
            }

            public Task StartSyncAsync(CancellationToken cancellationToken = default)
            {
                StartSyncCalls++;
                StartSyncStarted?.TrySetResult();
                return StartSyncRelease?.Task ?? Task.CompletedTask;
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                SyncNowCalls++;
                LastSyncNowPairId = syncPairId;
                SyncNowStarted?.TrySetResult();
                if (SyncNowException is not null)
                {
                    throw SyncNowException;
                }

                return SyncNowRelease?.Task ?? Task.CompletedTask;
            }

            public Task SyncNowAsync(
                Guid syncPairId,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                LastSyncNowRequest = request;
                return SyncNowAsync(syncPairId, cancellationToken);
            }

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopSyncAsync(CancellationToken cancellationToken = default)
            {
                StopSyncCalls++;
                return Task.CompletedTask;
            }

            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            private static AuthSession CreateSession(string username)
            {
                string normalized = username.Trim();
                string email = normalized.Contains('@', StringComparison.Ordinal)
                    ? normalized
                    : normalized + "@example.test";
                return new AuthSession(Guid.NewGuid(), normalized, email, isTotpEnabled: false);
            }

            private static TokenPairDto CreateTokenPair(string username)
            {
                string normalized = username.Trim();
                return new TokenPairDto
                {
                    AccessToken = "access-token-" + normalized,
                    RefreshToken = "refresh-token-" + normalized,
                };
            }
        }
    }
}
