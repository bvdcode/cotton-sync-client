// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private bool _isTokenStorageChoiceVisible;
        private string _tokenStorageFailureDetails = string.Empty;

        public bool IsTokenStorageChoiceVisible
        {
            get => _isTokenStorageChoiceVisible;
            private set
            {
                if (SetProperty(ref _isTokenStorageChoiceVisible, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public string TokenStorageFailureDetails
        {
            get => _tokenStorageFailureDetails;
            private set => SetProperty(ref _tokenStorageFailureDetails, value);
        }

        public string UnencryptedTokenStorageMessage => "Or save your sign-in without encryption in "
            + Path.Combine(DataDirectory, "tokens.json")
            + ". Anyone who can read this file can access your account.";

        public AsyncRelayCommand RetryTokenStorageCommand { get; private set; } = null!;

        public AsyncRelayCommand UseUnencryptedTokenStorageCommand { get; private set; } = null!;

        private void InitializeTokenStorageCommands()
        {
            RetryTokenStorageCommand = new AsyncRelayCommand(RetryTokenStorageAsync, () => !IsBusy, HandleCommandError);
            UseUnencryptedTokenStorageCommand = new AsyncRelayCommand(UseUnencryptedTokenStorageAsync,
                () => !IsBusy && IsTokenStorageChoiceVisible, HandleCommandError);
        }

        private async Task CheckTokenStorageAsync()
        {
            if (IsSignedIn)
            {
                return;
            }

            DesktopTokenStorageCapabilitySnapshot status = await _controller.CheckTokenStorageAsync().ConfigureAwait(true);
            IsTokenStorageChoiceVisible = OperatingSystem.IsLinux() && !status.IsUsable;
            TokenStorageFailureDetails = status.Details;
            OnPropertyChanged(nameof(UnencryptedTokenStorageMessage));
        }

        private async Task RetryTokenStorageAsync()
        {
            IsBusy = true;
            try
            {
                await CheckTokenStorageAsync().ConfigureAwait(true);
                if (!IsTokenStorageChoiceVisible && HasStoredSession)
                {
                    await RetryStoredSessionAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task UseUnencryptedTokenStorageAsync()
        {
            Task retry = CancelStoredSessionRetry();
            await StoredSessionRestoreRetryCoordinator.IgnoreCancellationAsync(retry).ConfigureAwait(true);
            IsBusy = true;
            try
            {
                await _controller.ApproveUnencryptedTokenStorageAsync().ConfigureAwait(true);
                HasStoredSession = false;
                StoredSessionRestoreMessage = string.Empty;
                ActionRequiredMessage = string.Empty;
                IsTokenStorageChoiceVisible = false;
                GlobalStatus = "Ready to sign in";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
