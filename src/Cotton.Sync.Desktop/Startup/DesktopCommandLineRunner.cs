// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sdk;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private const int FinalConvergencePasses = 3;
        private const string LocalUploadPath = "local-upload.txt";
        private const string LocalRenamedPath = "local-renamed.txt";
        private const string RemoteOriginPath = "remote-origin.txt";
        private const string RemoteRenamedPath = "remote-renamed.txt";
        private const string PreExistingClientAPath = "pre-existing/client-a/original-a.txt";
        private const string PreExistingClientBPath = "pre-existing/client-b/original-b.txt";
        private static readonly Guid ShellShareLinkSmokePairId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly TimeSpan DesktopLocalQuietWindow = TimeSpan.FromMilliseconds(2300);
        private static readonly TimeSpan InitialConvergenceTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PropagationTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PropagationPollInterval = TimeSpan.FromSeconds(1);
        private const int InitialConvergenceSyncRefreshInterval = 10;



























































































































    }
}
