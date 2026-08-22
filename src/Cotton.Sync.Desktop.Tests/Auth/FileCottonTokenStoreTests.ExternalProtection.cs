// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Diagnostics;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    public partial class FileCottonTokenStoreTests
    {
        [Test]
        public async Task ClearAsync_DeletesExternalProtectedPayloadWhenSupported()
        {
            string directory = CreateTempDirectory();
            try
            {
                string path = Path.Combine(directory, "tokens.json");
                RecordingDeletableTokenPayloadProtector protector = new RecordingDeletableTokenPayloadProtector("external-scheme");
                FileCottonTokenStore store = new FileCottonTokenStore(path, protector);

                await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                });

                Assert.That(protector.StoredPayloadIds, Has.Count.EqualTo(1));
                string savedPayloadId = protector.StoredPayloadIds[0];
                await store.ClearAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(path), Is.False);
                    Assert.That(protector.DeletedPayloadIds, Is.EqualTo(new[] { savedPayloadId }));
                });
            }
            finally
            {
                DeleteTempDirectory(directory);
            }
        }

        [Test]
        public async Task SaveAsync_DeletesPreviousExternalProtectedPayloadAfterOverwrite()
        {
            string directory = CreateTempDirectory();
            try
            {
                string path = Path.Combine(directory, "tokens.json");
                RecordingDeletableTokenPayloadProtector protector = new RecordingDeletableTokenPayloadProtector("external-scheme");
                FileCottonTokenStore store = new FileCottonTokenStore(path, protector);

                await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "first-access-token",
                    RefreshToken = "first-refresh-token",
                });
                Assert.That(protector.StoredPayloadIds, Has.Count.EqualTo(1));
                string firstPayloadId = protector.StoredPayloadIds[0];

                await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "second-access-token",
                    RefreshToken = "second-refresh-token",
                });
                TokenPairDto? loaded = await store.GetAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(protector.StoredPayloadIds, Has.Count.EqualTo(1));
                    Assert.That(protector.StoredPayloadIds, Does.Not.Contain(firstPayloadId));
                    Assert.That(protector.DeletedPayloadIds, Is.EqualTo(new[] { firstPayloadId }));
                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded!.AccessToken, Is.EqualTo("second-access-token"));
                    Assert.That(loaded.RefreshToken, Is.EqualTo("second-refresh-token"));
                });
            }
            finally
            {
                DeleteTempDirectory(directory);
            }
        }

        [Test]
        public async Task SaveAsync_RecordsRefreshSuccessWhenExistingTokensAreReplacedAfterUnauthorizedChallenge()
        {
            string directory = CreateTempDirectory();
            try
            {
                string path = Path.Combine(directory, "tokens.json");
                FileCottonTokenStore store = CreateStore(path);
                await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "first-access-token",
                    RefreshToken = "first-refresh-token",
                });
                DesktopAuthDiagnosticsState.RecordUnauthorizedChallenge();

                await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "second-access-token",
                    RefreshToken = "second-refresh-token",
                });

                DesktopAuthDiagnosticsSnapshot snapshot = DesktopAuthDiagnosticsState.Snapshot();
                Assert.Multiple(() =>
                {
                    Assert.That(snapshot.LastTokenRefreshStatus, Is.EqualTo("succeeded"));
                    Assert.That(snapshot.TokenSaveCount, Is.EqualTo(2));
                    Assert.That(snapshot.TokenRefreshSaveCount, Is.EqualTo(1));
                    Assert.That(snapshot.LastUnauthorizedChallengeAtUtc, Is.Not.Null);
                    Assert.That(snapshot.LastTokenRefreshAtUtc, Is.Not.Null);
                });
            }
            finally
            {
                DeleteTempDirectory(directory);
            }
        }

        [Test]
        public async Task SaveAsync_DeletesNewExternalProtectedPayloadWhenCommitFails()
        {
            string directory = CreateTempDirectory();
            try
            {
                RecordingDeletableTokenPayloadProtector protector = new RecordingDeletableTokenPayloadProtector("external-scheme");
                FileCottonTokenStore store = new FileCottonTokenStore(directory, protector);

                Exception? exception = Assert.CatchAsync(async () => await store.SaveAsync(new TokenPairDto
                {
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                }));

                Assert.Multiple(() =>
                {
                    Assert.That(exception, Is.TypeOf<IOException>().Or.TypeOf<UnauthorizedAccessException>());
                    Assert.That(protector.StoredPayloadIds, Is.Empty);
                    Assert.That(protector.DeletedPayloadIds, Is.EqualTo(new[] { "payload-1" }));
                });
            }
            finally
            {
                DeleteTempDirectory(directory);
            }
        }
    }
}
