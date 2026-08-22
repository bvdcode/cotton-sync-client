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
        private class FakeAutostartService : IAutostartService
        {
            public bool IsSupported => true;

            public int IsEnabledCalls { get; private set; }

            public int SetEnabledCalls { get; private set; }

            public bool? LastSetEnabled { get; private set; }

            public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            {
                IsEnabledCalls++;
                return Task.FromResult(LastSetEnabled == true);
            }

            public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                SetEnabledCalls++;
                LastSetEnabled = enabled;
                return Task.CompletedTask;
            }
        }

        private class FakePlatformCommandService : IPlatformCommandService
        {
            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
