// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesAdapterTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-adapter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
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
        public void CreateFilePlaceholder_RegistersSyncRootAndCreatesChildPlaceholder()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string root = Path.Combine(_tempDirectory, "root");
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);
            string fileIdentity = Encoding.UTF8.GetString(nativeApi.Placeholders[0].FileIdentity);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Registrations[0].LocalRootPath, Is.EqualTo(Path.GetFullPath(root)));
                Assert.That(nativeApi.Registrations[0].ProviderName, Is.EqualTo(WindowsCloudFilesAdapter.ProviderName));
                Assert.That(nativeApi.Registrations[0].SyncRootIdentity, Is.Not.Empty);
                Assert.That(nativeApi.Placeholders[0].BaseDirectoryPath, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "Projects")));
                Assert.That(nativeApi.Placeholders[0].RelativeFileName, Is.EqualTo("remote-only.txt"));
                Assert.That(nativeApi.Placeholders[0].FileSizeBytes, Is.EqualTo(12));
                Assert.That(nativeApi.Placeholders[0].FileIdentity, Is.EqualTo(result.PlaceholderIdentity));
                Assert.That(fileIdentity, Does.Contain("\"relativePath\":\"Projects/remote-only.txt\""));
                Assert.That(fileIdentity, Does.Contain("\"nodeFileId\":\"33333333-3333-3333-3333-333333333333\""));
                Assert.That(fileIdentity, Does.Contain("\"contentHash\":\"hash\""));
                Assert.That(fileIdentity, Does.Contain("\"eTag\":\"etag\""));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(Directory.Exists(Path.Combine(root, "Projects")), Is.True);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsDotSegmentsBeforeNativeCalls()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), @"Projects\..\outside.txt");

            Assert.Throws<SyncPathValidationException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsOversizedIdentityBeforeNativeCalls()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            string longPath = string.Join("/", Enumerable.Range(0, 24).Select(index => "segment-" + index.ToString("D2").PadRight(180, 'x'))) + "/file.txt";
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), longPath);

            InvalidOperationException? exception =
                Assert.Throws<InvalidOperationException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("4 KB"));
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsInvalidSyncPairIdBeforeNativeCalls()
        {
            var nativeApi = new FakeCloudFilesNativeApi();
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), "remote-only.txt", syncPairId: "not-a-guid");

            ArgumentException? exception =
                Assert.Throws<ArgumentException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("sync pair id"));
                Assert.That(nativeApi.Registrations, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_PropagatesNativeCloudFilesFailures()
        {
            var nativeApi = new FakeCloudFilesNativeApi
            {
                RegisterException = new WindowsCloudFilesNativeException("CfRegisterSyncRoot", unchecked((int)0x8007017C)),
            };
            var adapter = new WindowsCloudFilesAdapter(CreatePolicy(), nativeApi);
            RemoteFilePlaceholderRequest request = CreateRequest(Path.Combine(_tempDirectory, "root"), "remote-only.txt");

            WindowsCloudFilesNativeException? exception =
                Assert.Throws<WindowsCloudFilesNativeException>(() => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Operation, Is.EqualTo("CfRegisterSyncRoot"));
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        private WindowsVirtualFilesRootSafetyPolicy CreatePolicy()
        {
            return new WindowsVirtualFilesRootSafetyPolicy(
                _ => string.Empty,
                () => _tempDirectory);
        }

        private static RemoteFilePlaceholderRequest CreateRequest(
            string localRootPath,
            string relativePath,
            string syncPairId = "11111111-1111-1111-1111-111111111111")
        {
            return new RemoteFilePlaceholderRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = 12,
                    ContentHash = "hash",
                    ETag = "etag",
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private sealed class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesNativeSyncRootRegistration> Registrations { get; } = [];

            public List<WindowsCloudFilesNativePlaceholder> Placeholders { get; } = [];

            public Exception? RegisterException { get; set; }

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
                Registrations.Add(registration);
                if (RegisterException is not null)
                {
                    throw RegisterException;
                }
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                Placeholders.Add(placeholder);
            }
        }
    }
}
