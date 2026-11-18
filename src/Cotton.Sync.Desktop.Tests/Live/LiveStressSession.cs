// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal partial class LiveStressSession : IAsyncDisposable
    {
        private readonly HttpClient _http = new();
        private readonly string _server;
        private readonly string _username;
        private readonly string _password;
        private readonly string _remotePath = "/" + LiveTestDataCleanup.NamespacePrefix + Guid.NewGuid().ToString("N");
        private readonly LiveTestDataCleanup _cleanup;
        private readonly CottonCloudClient _admin;
        private NodeDto? _remoteRoot;
        private long _initialUsedBytes;

        public LiveStressSession()
        {
            _server = RequiredEnvironment("COTTON_SYNC_QA_SERVER");
            _username = RequiredEnvironment("COTTON_SYNC_QA_USERNAME");
            _password = RequiredEnvironment("COTTON_SYNC_QA_PASSWORD");
            string evidenceRoot = RequiredEnvironment("COTTON_SYNC_STRESS_DIRECTORY");
            DirectoryPath = Path.Combine(evidenceRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            FileCottonTokenStore tokens = new(Path.Combine(DirectoryPath, "admin-tokens.json"));
            _admin = new CottonCloudClient(_http, tokens, new CottonSdkOptions { BaseAddress = new Uri(_server) });
            _cleanup = new LiveTestDataCleanup(_admin, _http, tokens, new Uri(_server));
            First = CreateController("first", FirstGate);
            Second = CreateController("second", SecondGate);
        }

        public string DirectoryPath { get; }
        public string FirstRoot => Path.Combine(DirectoryPath, "first-files");
        public string SecondRoot => Path.Combine(DirectoryPath, "second-files");
        public LiveTransferGate FirstGate { get; } = new();
        public LiveTransferGate SecondGate { get; } = new();
        public DesktopShellController First { get; private set; }
        public DesktopShellController Second { get; }

        public async Task StartAsync(CancellationToken cancellationToken, long requiredStorageBytes = 768L * 1024 * 1024)
        {
            await _admin.Auth.LoginAsync(new LoginRequestDto { Username = _username, Password = _password },
                cancellationToken);
            if (Environment.GetEnvironmentVariable("COTTON_SYNC_QA_CLEANUP_ABANDONED") == "1")
            {
                await _cleanup.ClearAbandonedAsync(cancellationToken);
            }
            _initialUsedBytes = await _cleanup.RequireAvailableBytesAsync(requiredStorageBytes, cancellationToken);
            NodeDto root = await _admin.Nodes.ResolveAsync(cancellationToken: cancellationToken);
            _remoteRoot = await _admin.Nodes.CreateAsync(root.Id, _remotePath.TrimStart('/'), cancellationToken);
            Directory.CreateDirectory(FirstRoot);
            Directory.CreateDirectory(SecondRoot);
            await SignInAsync(First, cancellationToken);
            await SignInAsync(Second, cancellationToken);
            await First.AddSyncPairAsync(new DesktopSyncPairRequest(FirstRoot, _remotePath, SyncPairMode.FullMirror),
                cancellationToken);
            await Second.AddSyncPairAsync(new DesktopSyncPairRequest(SecondRoot, _remotePath, SyncPairMode.FullMirror),
                cancellationToken);
        }

        public async Task RestartFirstAsync(CancellationToken cancellationToken)
        {
            await First.DisposeAsync();
            FirstGate.Release();
            First = CreateController("first", FirstGate);
            DesktopShellSnapshot snapshot = await First.LoadAsync(cancellationToken);
            Assert.That(snapshot.IsSignedIn, Is.True, "Saved session must survive restart without signing in again.");
        }

        public async Task ConvergeAsync(IReadOnlyDictionary<string, string> expected,
            CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            TimeSpan convergenceTimeout = timeout ?? TimeSpan.FromMinutes(5);
            DateTime deadline = DateTime.UtcNow + convergenceTimeout;
            int polls = 0;
            while (DateTime.UtcNow < deadline)
            {
                DesktopShellSnapshot first = await First.LoadAsync(cancellationToken);
                DesktopShellSnapshot second = await Second.LoadAsync(cancellationToken);
                bool idle = IsIdle(first) && IsIdle(second);
                bool paths = HaveExpectedPaths(FirstRoot, expected.Keys) && HaveExpectedPaths(SecondRoot, expected.Keys);
                if (++polls % 10 == 0)
                {
                    TestContext.Progress.WriteLine($"Convergence: first={first.SyncPairs.Single().Status}, "
                        + $"second={second.SyncPairs.Single().Status}, paths={paths}, expected={expected.Count}, "
                        + $"firstError={first.SyncPairs.Single().LastError}, secondError={second.SyncPairs.Single().LastError}");
                }
                if (idle && paths && await HaveExpectedContentAsync(expected, cancellationToken))
                {
                    DesktopShellSnapshot verifiedFirst = await First.LoadAsync(cancellationToken);
                    DesktopShellSnapshot verifiedSecond = await Second.LoadAsync(cancellationToken);
                    if (IsIdle(verifiedFirst) && IsIdle(verifiedSecond))
                    {
                        TestContext.Progress.WriteLine($"Exact content and idle state verified: {expected.Count} files per client.");
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            await WriteConvergenceFailureAsync(expected, cancellationToken);
            Assert.Fail("Clients did not converge to the exact expected tree within " + convergenceTimeout + ".");
        }

        private async Task<bool> HaveExpectedContentAsync(IReadOnlyDictionary<string, string> expected,
            CancellationToken cancellationToken)
        {
            foreach ((string path, string hash) in expected)
            {
                if (await LiveStressFiles.HashAsync(LiveStressFiles.FullPath(FirstRoot, path), cancellationToken) != hash
                    || await LiveStressFiles.HashAsync(LiveStressFiles.FullPath(SecondRoot, path), cancellationToken) != hash)
                {
                    return false;
                }
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            FirstGate.Release();
            SecondGate.Release();
            FirstGate.SetOffline(false);
            SecondGate.SetOffline(false);
            try
            {
                await First.SignOutAsync();
                await Second.SignOutAsync();
            }
            finally
            {
                await First.DisposeAsync();
                await Second.DisposeAsync();
                try
                {
                    if (_remoteRoot is not null)
                    {
                        await _cleanup.ClearAsync(_remoteRoot.Id, CancellationToken.None);
                        long usedBytes = await _cleanup.ReadUsedBytesAsync(CancellationToken.None);
                        TestContext.Progress.WriteLine($"Isolated test data removed; storage before={_initialUsedBytes}, after={usedBytes} bytes.");
                        if (Environment.GetEnvironmentVariable("COTTON_SYNC_QA_CLEANUP_ABANDONED") == "1")
                        {
                            Assert.That(usedBytes, Is.EqualTo(_initialUsedBytes), "The exclusive QA account must not retain test data.");
                        }
                    }
                }
                finally
                {
                    await _admin.Auth.LogoutAsync();
                    await _admin.DisposeAsync();
                    _http.Dispose();
                }
            }
        }

        private DesktopShellController CreateController(string name, LiveTransferGate gate)
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(Path.Combine(DirectoryPath, name + "-state"));
            DesktopTraceLogging.Install(paths);
            LiveSmokePlatformCommandService commands = new(TestContext.Progress, TimeSpan.Zero);
            return new DesktopShellController(paths,
                new DesktopSyncApplicationFactory(paths, new DesktopTraceLoggerFactory(), commands,
                    () => new HttpClient(new LiveTransferHandler(gate, DesktopHttpClientFactory.CreateHandler()))
                    {
                        Timeout = TimeSpan.FromSeconds(30),
                    }),
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                new SqliteSyncPairSettingsStore(paths.AppDatabasePath), commands,
                new UnsupportedAutostartService());
        }

        private Task<AuthSession> SignInAsync(DesktopShellController controller, CancellationToken cancellationToken)
        {
            return controller.SignInAsync(new DesktopSignInRequest(_server, _username, _password, null), cancellationToken);
        }

        private static bool IsIdle(DesktopShellSnapshot snapshot)
        {
            return snapshot.SyncPairs.Count == 1 && snapshot.SyncPairs[0].Status == "Idle"
                && snapshot.SyncPairs[0].LastError is null;
        }

        private static bool HaveExpectedPaths(string root, IEnumerable<string> expected)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !SyncPathIgnoreRules.ShouldIgnore(path))
                .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal));
        }

        private static string RequiredEnvironment(string name)
        {
            return Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException("Required environment variable: " + name);
        }
    }
}
