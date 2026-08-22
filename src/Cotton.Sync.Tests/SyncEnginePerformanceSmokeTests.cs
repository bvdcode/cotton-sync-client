// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests
{
    public partial class SyncEnginePerformanceSmokeTests
    {
        private const long MiB = 1024L * 1024L;
        private static readonly Guid RemoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private string _root = string.Empty;
        private string _databasePath = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-sync-performance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, ".cotton-sync", "state.sqlite");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

    }
}
