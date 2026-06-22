// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsVirtualFilesRootSafetyPolicyTests
    {
        private string _repoDirectory = string.Empty;
        private string _repoSubdirectory = string.Empty;
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-vfs-root-safety-" + Guid.NewGuid().ToString("N"));
            _repoDirectory = Path.Combine(_tempDirectory, "repo");
            _repoSubdirectory = Path.Combine(_repoDirectory, "src");
            Directory.CreateDirectory(_repoSubdirectory);
            Directory.CreateDirectory(Path.Combine(_repoDirectory, ".git"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void Validate_AcceptsIsolatedNonProtectedRoot()
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();
            string root = Path.Combine(_tempDirectory, "CottonSyncVfsQa", "root");

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(root);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.True);
                Assert.That(result.Issue, Is.EqualTo(WindowsVirtualFilesRootSafetyIssue.None));
                Assert.That(result.FullPath, Is.EqualTo(Path.GetFullPath(root)));
            });
        }

        [Test]
        public void Validate_AcceptsCanonicalSDriveQaRoot()
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(@"S:\CottonSyncVfsQa\root");

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.True);
                Assert.That(result.Issue, Is.EqualTo(WindowsVirtualFilesRootSafetyIssue.None));
                Assert.That(result.FullPath, Is.EqualTo(@"S:\CottonSyncVfsQa\root"));
            });
        }

        [Test]
        public void Validate_AcceptsDesktopFolderUnderUserProfile()
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(@"C:\Users\Example\Desktop");

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.True);
                Assert.That(result.Issue, Is.EqualTo(WindowsVirtualFilesRootSafetyIssue.None));
                Assert.That(result.FullPath, Is.EqualTo(@"C:\Users\Example\Desktop"));
            });
        }

        [TestCase("", (int)WindowsVirtualFilesRootSafetyIssue.EmptyPath)]
        [TestCase("CottonSyncVfsQa", (int)WindowsVirtualFilesRootSafetyIssue.RelativePath)]
        [TestCase(@"C:\", (int)WindowsVirtualFilesRootSafetyIssue.DriveRoot)]
        [TestCase(@"C:\Users\Example", (int)WindowsVirtualFilesRootSafetyIssue.UserProfileRoot)]
        [TestCase(@"C:\Windows", (int)WindowsVirtualFilesRootSafetyIssue.WindowsRoot)]
        [TestCase(@"C:\Program Files", (int)WindowsVirtualFilesRootSafetyIssue.ProgramFilesRoot)]
        [TestCase(@"C:\Program Files (x86)", (int)WindowsVirtualFilesRootSafetyIssue.ProgramFilesRoot)]
        public void Validate_RejectsUnsafeRoot(string root, int expectedIssueValue)
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();
            var expectedIssue = (WindowsVirtualFilesRootSafetyIssue)expectedIssueValue;

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(root);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.False);
                Assert.That(result.Issue, Is.EqualTo(expectedIssue));
                Assert.That(result.Details, Is.Not.Empty);
            });
        }

        [Test]
        public void Validate_RejectsCurrentRepositoryRoot()
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(_repoDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.False);
                Assert.That(result.Issue, Is.EqualTo(WindowsVirtualFilesRootSafetyIssue.RepositoryRoot));
            });
        }

        [Test]
        public void Validate_RejectsPathInsideCurrentRepositoryRoot()
        {
            WindowsVirtualFilesRootSafetyPolicy policy = CreatePolicy();

            WindowsVirtualFilesRootSafetyResult result = policy.Validate(Path.Combine(_repoDirectory, "sync-root"));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSafe, Is.False);
                Assert.That(result.Issue, Is.EqualTo(WindowsVirtualFilesRootSafetyIssue.RepositoryRoot));
            });
        }

        [Test]
        public void CreateRegistration_RequiresWindowsVirtualFilesMode()
        {
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy());
            SyncPairSettings syncPair = CreatePair(Path.Combine(_tempDirectory, "CottonSyncVfsQa", "root"));
            syncPair.Mode = SyncPairMode.FullMirror;

            Assert.Throws<InvalidOperationException>(() => adapter.CreateRegistration(syncPair));
        }

        [Test]
        public void CreateRegistration_RejectsUnsafeRootBeforeCloudFilesRegistration()
        {
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy());
            SyncPairSettings syncPair = CreatePair(_repoDirectory);

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.CreateRegistration(syncPair));

            Assert.That(exception?.Message, Does.Contain("source repository"));
        }

        [Test]
        public void CreateRegistration_ReturnsStableProviderIdentityAndNormalizedRoot()
        {
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy());
            string root = Path.Combine(_tempDirectory, "CottonSyncVfsQa", "root") + Path.DirectorySeparatorChar;
            SyncPairSettings syncPair = CreatePair(root);

            WindowsCloudFilesSyncRootRegistration registration = adapter.CreateRegistration(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(registration.SyncPairId, Is.EqualTo(syncPair.Id));
                Assert.That(registration.ProviderId, Is.EqualTo(WindowsCloudFilesAdapter.ProviderId));
                Assert.That(registration.DisplayName, Is.EqualTo("Documents"));
                Assert.That(registration.LocalRootPath, Is.EqualTo(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)));
            });
        }

        private WindowsVirtualFilesRootSafetyPolicy CreatePolicy()
        {
            return new WindowsVirtualFilesRootSafetyPolicy(GetSpecialFolderPath, () => _repoSubdirectory);
        }

        private static string GetSpecialFolderPath(Environment.SpecialFolder folder)
        {
            return folder switch
            {
                Environment.SpecialFolder.UserProfile => @"C:\Users\Example",
                Environment.SpecialFolder.Windows => @"C:\Windows",
                Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
                Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
                _ => string.Empty,
            };
        }

        private static SyncPairSettings CreatePair(string localRootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = " Documents ",
                LocalRootPath = localRootPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }
    }
}
