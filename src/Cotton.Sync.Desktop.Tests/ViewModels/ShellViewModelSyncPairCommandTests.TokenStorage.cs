// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [Test]
        public async Task InitializeAsync_WorkingTokenStorage_DoesNotShowStorageChoice()
        {
            FakeDesktopShellController controller = new(CreateSignedOutSnapshot());
            using ShellViewModel model = CreateViewModel(controller);

            await model.InitializeAsync();

            Assert.That(model.IsTokenStorageChoiceVisible, Is.False);
            Assert.That(controller.UnencryptedStorageApprovals, Is.Zero);
        }

        [Test]
        public async Task InitializeAsync_NoSecureStorage_RequiresExplicitApprovalAndShowsExactPath()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("Linux storage choices are not shown on Windows.");
                return;
            }

            FakeDesktopShellController controller = new(CreateSignedOutSnapshot())
            {
                TokenStorageStatus = new("unavailable", false, "KWallet is unavailable on this desktop."),
            };
            using ShellViewModel model = CreateViewModel(controller);
            await model.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(model.IsTokenStorageChoiceVisible, Is.True);
                Assert.That(model.TokenStorageFailureDetails, Does.Contain("KWallet"));
                Assert.That(model.UnencryptedTokenStorageMessage, Does.Contain(Path.Combine(model.DataDirectory, "tokens.json")));
                Assert.That(model.UnencryptedTokenStorageMessage, Does.Contain("without encryption"));
                Assert.That(controller.UnencryptedStorageApprovals, Is.Zero);
            });

            model.UseUnencryptedTokenStorageCommand.Execute(null);
            await WaitForAsync(() => !model.UseUnencryptedTokenStorageCommand.IsRunning);

            Assert.That(controller.UnencryptedStorageApprovals, Is.EqualTo(1));
            Assert.That(controller.SignOutCalls, Is.Zero);
            Assert.That(model.IsTokenStorageChoiceVisible, Is.False);
        }

        [Test]
        public async Task RetryTokenStorage_WhenKeyringBecomesAvailable_HidesChoiceWithoutPlaintextApproval()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("Linux storage choices are not shown on Windows.");
                return;
            }

            FakeDesktopShellController controller = new(CreateSignedOutSnapshot())
            {
                TokenStorageStatus = new("unavailable", false, "KWallet is locked."),
            };
            using ShellViewModel model = CreateViewModel(controller);
            await model.InitializeAsync();
            controller.TokenStorageStatus = new("kwallet", true, "KWallet verified");

            model.RetryTokenStorageCommand.Execute(null);
            await WaitForAsync(() => !model.RetryTokenStorageCommand.IsRunning);

            Assert.That(model.IsTokenStorageChoiceVisible, Is.False);
            Assert.That(controller.UnencryptedStorageApprovals, Is.Zero);
        }

        private partial class FakeDesktopShellController
        {
            public DesktopTokenStorageCapabilitySnapshot TokenStorageStatus { get; set; } = new("system", true, "verified");
            public int UnencryptedStorageApprovals { get; private set; }

            public Task<DesktopTokenStorageCapabilitySnapshot> CheckTokenStorageAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(TokenStorageStatus);

            public Task ApproveUnencryptedTokenStorageAsync(CancellationToken cancellationToken = default)
            {
                UnencryptedStorageApprovals++;
                TokenStorageStatus = new("restricted-file-v1", false, "Unencrypted file", IsUserApproved: true);
                return Task.CompletedTask;
            }
        }
    }
}
