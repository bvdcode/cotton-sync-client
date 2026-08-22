// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        private readonly Guid _remoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private string _root = string.Empty;

        private string _databasePath = string.Empty;


        public enum MatrixFileState
        {
            Missing,
            Baseline,
            Changed,
        }
    }
}
