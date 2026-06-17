// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsNativePathTests
    {
        [Test]
        public void ToWin32FilePath_AddsExtendedPrefixToDrivePath()
        {
            string path = WindowsNativePath.ToWin32FilePath(@"S:\Cloud\Library\file.txt");

            Assert.That(path, Is.EqualTo(@"\\?\S:\Cloud\Library\file.txt"));
        }

        [Test]
        public void ToWin32FilePath_AddsExtendedUncPrefixToUncPath()
        {
            string path = WindowsNativePath.ToWin32FilePath(@"\\server\share\Cloud\file.txt");

            Assert.That(path, Is.EqualTo(@"\\?\UNC\server\share\Cloud\file.txt"));
        }

        [Test]
        public void ToWin32FilePath_MapsDevicePathToGlobalRootPath()
        {
            string path = WindowsNativePath.ToWin32FilePath(@"\Device\HarddiskVolume1\Cloud\file.txt");

            Assert.That(path, Is.EqualTo(@"\\?\GLOBALROOT\Device\HarddiskVolume1\Cloud\file.txt"));
        }

        [Test]
        public void ToWin32FilePath_LeavesAlreadyExtendedPathUnchanged()
        {
            string path = WindowsNativePath.ToWin32FilePath(@"\\?\S:\Cloud\file.txt");

            Assert.That(path, Is.EqualTo(@"\\?\S:\Cloud\file.txt"));
        }
    }
}
