// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Auth;
using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    public class DesktopTokenStorageTests
    {
        private string _directory = null!;
        private string TokenPath => Path.Combine(_directory, "tokens.json");

        [SetUp]
        public void SetUp() => _directory = Directory.CreateTempSubdirectory("token-storage-").FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        [Test]
        public async Task SelectAsync_UsesFirstVerifiedProviderAndStopsProbing()
        {
            StorageTestProtector broken = new("broken");
            StorageTestProtector working = new("working");
            StorageTestProtector unused = new("unused");
            List<string> probes = [];
            DesktopTokenStorage storage = new(TokenPath, [broken, working, unused], (provider, token) =>
            {
                probes.Add(provider.Scheme);
                return Task.FromResult(new DesktopTokenStorageCapabilitySnapshot(provider.Scheme,
                    provider.Scheme == "working", provider.Scheme));
            });

            Assert.That(await storage.SelectAsync(CancellationToken.None), Is.SameAs(working));
            Assert.That(await storage.SelectAsync(CancellationToken.None), Is.SameAs(working));
            Assert.That(probes, Is.EqualTo(new[] { "broken", "working" }));
        }

        [Test]
        public void SelectAsync_WhenAllProvidersFail_DoesNotApproveOrWritePlaintext()
        {
            DesktopTokenStorage storage = new(TokenPath, []);
            FileCottonTokenStore store = new(TokenPath, storage);

            Assert.ThrowsAsync<TokenStorageUnavailableException>(async () => await store.SaveAsync(Tokens()));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public async Task ExplicitConsent_SurvivesRestartAndKeepsFilesPrivate()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("The unencrypted storage option is Linux-specific.");
                return;
            }

            DesktopTokenStorage storage = new(TokenPath, []);
            await storage.ApproveUnencryptedFileAsync(CancellationToken.None);
            FileCottonTokenStore store = new(TokenPath, storage);
            await store.SaveAsync(Tokens());
            DesktopTokenStorage restarted = new(TokenPath, []);
            DesktopTokenStorageCapabilitySnapshot status = await restarted.VerifyAsync(CancellationToken.None);
            TokenPairDto? restored = await new FileCottonTokenStore(TokenPath, restarted).GetAsync();
            UnixFileMode tokenMode = File.GetUnixFileMode(TokenPath);
            UnixFileMode consentMode = File.GetUnixFileMode(TokenPath + ".storage");

            Assert.Multiple(() =>
            {
                Assert.That(restored?.RefreshToken, Is.EqualTo("test-refresh"));
                Assert.That(status.IsReleaseSecure, Is.False);
                Assert.That(status.IsUserApproved, Is.True);
                Assert.That(status.IsUsable, Is.True);
                Assert.That(tokenMode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                Assert.That(consentMode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            });
        }

        [Test]
        public async Task GetAsync_UsesSavedProviderEvenWhenAnotherIsSelectedForNewTokens()
        {
            StorageTestProtector previous = new("previous");
            await new FileCottonTokenStore(TokenPath, previous).SaveAsync(Tokens());
            StorageTestProtector selected = new("selected");
            DesktopTokenStorage storage = new(TokenPath, [selected, previous], Verified);
            await storage.SelectAsync(CancellationToken.None);

            TokenPairDto? restored = await new FileCottonTokenStore(TokenPath, storage).GetAsync();

            Assert.That(restored?.RefreshToken, Is.EqualTo("test-refresh"));
            Assert.That(previous.Reads, Is.EqualTo(1));
            Assert.That(selected.Reads, Is.Zero);
        }

        [Test]
        public async Task GetAsync_WhenSavedProviderIsUnavailable_PreservesEnvelopeAndReportsFailure()
        {
            await new FileCottonTokenStore(TokenPath, new StorageTestProtector("missing")).SaveAsync(Tokens());
            byte[] before = await File.ReadAllBytesAsync(TokenPath);
            FileCottonTokenStore store = new(TokenPath, new DesktopTokenStorage(TokenPath, []));

            Assert.ThrowsAsync<TokenStorageUnavailableException>(async () => await store.GetAsync());
            Assert.That(await File.ReadAllBytesAsync(TokenPath), Is.EqualTo(before));
        }

        [Test]
        public void SelectAsync_WhenCancelled_DoesNotTryAnotherProvider()
        {
            int probes = 0;
            using CancellationTokenSource cancellation = new();
            DesktopTokenStorage storage = new(TokenPath, [new StorageTestProtector("first"), new StorageTestProtector("second")],
                (provider, token) =>
                {
                    probes++;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Verified(provider, token);
                });

            Assert.ThrowsAsync<OperationCanceledException>(async () => await storage.SelectAsync(cancellation.Token));
            Assert.That(probes, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        private static Task<DesktopTokenStorageCapabilitySnapshot> Verified(ITokenPayloadProtector provider, CancellationToken token)
            => Task.FromResult(new DesktopTokenStorageCapabilitySnapshot(provider.Scheme, true, "verified"));

        private static TokenPairDto Tokens() => new() { AccessToken = "test-access", RefreshToken = "test-refresh" };
    }
}
