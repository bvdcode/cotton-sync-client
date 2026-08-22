// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        private class InMemorySyncPairSettingsStore : ISyncPairSettingsStore
        {
            private readonly Dictionary<Guid, SyncPairSettings> _syncPairs = [];

            public int InitializeCallCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                InitializeCallCount++;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncPairSettings> syncPairs = _syncPairs.Values
                    .OrderBy(pair => pair.DisplayName, StringComparer.Ordinal)
                    .ToList();
                return Task.FromResult(syncPairs);
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                _syncPairs.TryGetValue(syncPairId, out SyncPairSettings? syncPair);
                return Task.FromResult(syncPair);
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                _syncPairs[syncPair.Id] = syncPair;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                _syncPairs.Remove(syncPairId);
                return Task.CompletedTask;
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            public int InitializeCallCount { get; private set; }

            public List<string> DeletedSyncPairIds { get; } = [];

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                InitializeCallCount++;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>([]);
            }

            public IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return EmptyEntries();
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<SyncStateEntry?>(null);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                DeletedSyncPairIds.Add(syncPairId);
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            private static async IAsyncEnumerable<SyncStateEntry> EmptyEntries()
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }
        }

        private class FakeSyncPairPrerequisiteValidator : ISyncPairPrerequisiteValidator
        {
            private readonly IReadOnlyList<SyncPairValidationError> _errors;

            public FakeSyncPairPrerequisiteValidator(IReadOnlyList<SyncPairValidationError> errors)
            {
                _errors = errors;
            }

            public int CallCount { get; private set; }

            public Task<IReadOnlyList<SyncPairValidationError>> ValidateAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(_errors);
            }
        }

        private class FakeAppPreferencesStore : IAppPreferencesStore
        {
            public AppPreferences Preferences { get; } = new();

            public int InitializeCallCount { get; private set; }

            public int SaveCallCount { get; private set; }

            public AppPreferences? SavedPreferences { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                InitializeCallCount++;
                return Task.CompletedTask;
            }

            public Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Preferences);
            }

            public Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
            {
                SaveCallCount++;
                SavedPreferences = preferences;
                return Task.CompletedTask;
            }
        }

        private class FakeAuthFlow : IAuthFlow
        {
            public AuthSession Session { get; } = new(Guid.NewGuid(), "vadim", "vadim@example.test", false);

            public int SignInCallCount { get; private set; }

            public int RestoreSessionCallCount { get; private set; }

            public int SignOutCallCount { get; private set; }

            public PasswordSignInRequest? LastSignInRequest { get; private set; }

            public Task<AuthSession> SignInAsync(
                PasswordSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                SignInCallCount++;
                LastSignInRequest = request;
                return Task.FromResult(Session);
            }

            public Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
            {
                RestoreSessionCallCount++;
                return Task.FromResult(Session);
            }

            public Task SignOutAsync(CancellationToken cancellationToken = default)
            {
                SignOutCallCount++;
                return Task.CompletedTask;
            }
        }

        private class FakeAppCodeBrowserAuthFlow : IAppCodeBrowserAuthFlow
        {
            public AuthSession Session { get; } = new(Guid.NewGuid(), "browser", "browser@example.test", false);

            public int SignInCallCount { get; private set; }

            public AppCodeBrowserSignInRequest? LastSignInRequest { get; private set; }

            public Task<AuthSession> SignInAsync(
                AppCodeBrowserSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                SignInCallCount++;
                LastSignInRequest = request;
                return Task.FromResult(Session);
            }
        }

    }
}
