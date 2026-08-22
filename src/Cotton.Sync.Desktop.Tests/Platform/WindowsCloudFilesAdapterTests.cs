// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public partial class WindowsCloudFilesAdapterTests
    {
        private const int HResultPathNotFound = unchecked((int)0x80070003);
        private const int HResultSharingViolation = unchecked((int)0x80070020);
        private const int HResultLockViolation = unchecked((int)0x80070021);
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint CfPlaceholderCreateFlagDisableOnDemandPopulation = 0x00000001;
        private const uint CfPlaceholderCreateFlagMarkInSync = 0x00000002;
        private const uint CfUpdateFlagVerifyInSync = 0x00000001;
        private const uint CfUpdateFlagMarkInSync = 0x00000002;
        private const uint CfUpdateFlagDehydrate = 0x00000004;
        private const uint CfUpdateFlagDisableOnDemandPopulation = 0x00000010;
        private const uint CfUpdateFlagAllowPartial = 0x00000400;
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
        public void CreatePlaceholderCreateFlags_ForDirectoryMarksFullyPopulated()
        {
            uint flags = InvokeNativeFlagFactory("CreatePlaceholderCreateFlags", isDirectory: true);

            Assert.That(
                flags,
                Is.EqualTo(CfPlaceholderCreateFlagMarkInSync | CfPlaceholderCreateFlagDisableOnDemandPopulation));
        }

        [Test]
        public void CreateUpdateFlags_ForDirectoryMarksFullyPopulated()
        {
            uint flags = InvokeNativeFlagFactory("CreateUpdateFlags", isDirectory: true);

            Assert.That(
                flags,
                Is.EqualTo(
                    CfUpdateFlagVerifyInSync
                    | CfUpdateFlagMarkInSync
                    | CfUpdateFlagDisableOnDemandPopulation));
        }

        [Test]
        public void CreateUpdateFlags_ForFileDehydratesStaleContentAndAllowsPartialUpdates()
        {
            uint flags = InvokeNativeFlagFactory("CreateUpdateFlags", isDirectory: false);

            Assert.That(
                flags,
                Is.EqualTo(
                    CfUpdateFlagVerifyInSync
                    | CfUpdateFlagMarkInSync
                    | CfUpdateFlagDehydrate
                    | CfUpdateFlagAllowPartial));
        }

        [Test]
        public void CreateReparseTagOpenFlags_IncludesBackupSemanticsForDirectories()
        {
            string directoryPath = Path.Combine(_tempDirectory, "directory-placeholder");
            Directory.CreateDirectory(directoryPath);

            uint flags = WindowsCloudFilesAdapter.CreateReparseTagOpenFlags(directoryPath);

            Assert.Multiple(() =>
            {
                Assert.That((flags & FileFlagOpenReparsePoint), Is.EqualTo(FileFlagOpenReparsePoint));
                Assert.That((flags & FileFlagBackupSemantics), Is.EqualTo(FileFlagBackupSemantics));
            });
        }

        [Test]
        public void CreateReparseTagOpenFlags_DoesNotIncludeBackupSemanticsForFiles()
        {
            string filePath = Path.Combine(_tempDirectory, "remote-only.txt");
            File.WriteAllText(filePath, string.Empty);

            uint flags = WindowsCloudFilesAdapter.CreateReparseTagOpenFlags(filePath);

            Assert.Multiple(() =>
            {
                Assert.That((flags & FileFlagOpenReparsePoint), Is.EqualTo(FileFlagOpenReparsePoint));
                Assert.That((flags & FileFlagBackupSemantics), Is.EqualTo(0));
            });
        }

        [Test]
        public void CreateReparseTagOpenPath_UsesExtendedSyntaxBeyondLegacyLimit()
        {
            const string extendedPathPrefix = "\\\\?\\";
            string longPath = "C:\\Cloud\\" + new string('a', 250) + "\\placeholder";

            string openPath = WindowsCloudFilesAdapter.CreateReparseTagOpenPath(longPath);

            Assert.Multiple(() =>
            {
                Assert.That(longPath.Length, Is.GreaterThan(260));
                Assert.That(openPath, Is.EqualTo(extendedPathPrefix + longPath));
            });
        }









    }
}
