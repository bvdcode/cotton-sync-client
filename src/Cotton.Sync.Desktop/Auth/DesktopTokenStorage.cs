// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Auth
{
    internal class DesktopTokenStorage
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly IReadOnlyList<ITokenPayloadProtector> _providers;
        private readonly string _consentPath;
        private readonly Func<ITokenPayloadProtector, CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>> _verify;
        private ITokenPayloadProtector? _selected;

        public DesktopTokenStorage(string tokenPath)
            : this(tokenPath, CreateProviders())
        {
        }

        internal DesktopTokenStorage(string tokenPath, IReadOnlyList<ITokenPayloadProtector> providers,
            Func<ITokenPayloadProtector, CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>>? verify = null)
        {
            TokenPath = Path.GetFullPath(tokenPath);
            _consentPath = TokenPath + ".storage";
            _providers = providers;
            _verify = verify ?? DesktopTokenStorageCapabilities.CreateVerifiedSnapshotAsync;
        }

        public string TokenPath { get; }

        public ITokenPayloadProtector Resolve(string scheme)
        {
            if (scheme == new RestrictedFileTokenPayloadProtector().Scheme)
            {
                return new RestrictedFileTokenPayloadProtector();
            }

            return _providers.FirstOrDefault(provider => provider.Scheme == scheme)
                ?? throw new TokenStorageUnavailableException("The saved session's token storage is unavailable: " + scheme);
        }

        public async Task<ITokenPayloadProtector> SelectAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_selected is not null)
                {
                    return _selected;
                }

                if (File.Exists(_consentPath)
                    && await File.ReadAllTextAsync(_consentPath, cancellationToken).ConfigureAwait(false)
                        == new RestrictedFileTokenPayloadProtector().Scheme)
                {
                    _selected = new RestrictedFileTokenPayloadProtector();
                    return _selected;
                }

                List<string> failures = [];
                foreach (ITokenPayloadProtector provider in _providers)
                {
                    DesktopTokenStorageCapabilitySnapshot status = await _verify(provider, cancellationToken).ConfigureAwait(false);
                    if (status.IsReleaseSecure)
                    {
                        _selected = provider;
                        Trace.TraceInformation("Selected token storage: {0}.", provider.Scheme);
                        return provider;
                    }

                    failures.Add(status.Details);
                }

                Trace.TraceWarning("Secure token storage providers are unavailable: {0}", string.Join("; ", failures));
                throw new TokenStorageUnavailableException("Secure sign-in storage is unavailable in this desktop session.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DesktopTokenStorageCapabilitySnapshot> VerifyAsync(CancellationToken cancellationToken)
        {
            try
            {
                ITokenPayloadProtector provider = await SelectAsync(cancellationToken).ConfigureAwait(false);
                DesktopTokenStorageCapabilitySnapshot result = DesktopTokenStorageCapabilities.CreateSnapshot(provider);
                return result with { IsUserApproved = provider is RestrictedFileTokenPayloadProtector };
            }
            catch (TokenStorageUnavailableException exception)
            {
                Trace.TraceWarning("Token storage selection failed: {0}", exception.Message);
                return new DesktopTokenStorageCapabilitySnapshot("unavailable", false, exception.Message);
            }
        }

        public async Task RetryAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _selected = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ApproveUnencryptedFileAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Unencrypted token storage is only offered on Linux.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_consentPath)!);
                FileStreamOptions options = new()
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Options = FileOptions.Asynchronous,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                await using FileStream stream = new(_consentPath, options);
                byte[] mode = System.Text.Encoding.UTF8.GetBytes(new RestrictedFileTokenPayloadProtector().Scheme);
                await stream.WriteAsync(mode, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _selected = new RestrictedFileTokenPayloadProtector();
                Trace.TraceInformation("Unencrypted token storage explicitly approved for this profile.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static IReadOnlyList<ITokenPayloadProtector> CreateProviders()
        {
            if (!OperatingSystem.IsLinux())
            {
                return [DesktopTokenPayloadProtectorFactory.CreateDefault()];
            }

            return [
                new LinuxKWalletTokenPayloadProtector(6),
                new LinuxKWalletTokenPayloadProtector(5),
                DesktopTokenPayloadProtectorFactory.CreateDefault(),
            ];
        }
    }
}
