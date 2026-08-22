// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Cli
{
    internal static partial class SyncCliCrudSmokeCommandRunner
    {
        private const int MaxFinalConvergencePasses = 6;

        private static readonly string LocalUploadPath = "local-upload.txt";

        private static readonly string LocalRenamedPath = "local-renamed.txt";

        private static readonly string RemoteOriginPath = "remote-origin.txt";

        private static readonly string RemoteRenamedPath = "remote-renamed.txt";
    }
}
