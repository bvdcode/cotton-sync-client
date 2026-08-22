// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public class DesktopUiBoundaryTests
    {
        [Test]
        public void ShellViewModel_UsesDesktopShellAbstractionsInsteadOfSyncEngine()
        {
            string source = File.ReadAllText(GetDesktopFilePath("ViewModels/ShellViewModel.cs"));
            string controllerContract = File.ReadAllText(GetDesktopFilePath("Shell/IDesktopShellController.cs"));
            string folderPickerContract = File.ReadAllText(GetDesktopFilePath("Platform/ILocalFolderPicker.cs"));
            string notificationContract = File.ReadAllText(GetDesktopFilePath("Platform/IDesktopNotificationService.cs"));
            string themeContract = File.ReadAllText(GetDesktopFilePath("Platform/IDesktopThemeService.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(controllerContract, Does.Contain("interface IDesktopShellController"));
                Assert.That(folderPickerContract, Does.Contain("interface ILocalFolderPicker"));
                Assert.That(notificationContract, Does.Contain("interface IDesktopNotificationService"));
                Assert.That(themeContract, Does.Contain("interface IDesktopThemeService"));
                Assert.That(source, Does.Contain("IDesktopShellController controller"));
                Assert.That(source, Does.Contain("ILocalFolderPicker folderPicker"));
                Assert.That(source, Does.Contain("IDesktopNotificationService notificationService"));
                Assert.That(source, Does.Contain("IDesktopThemeService themeService"));
                Assert.That(source, Does.Not.Contain("Cotton.Sync.SyncEngine"));
                Assert.That(source, Does.Not.Contain("SyncEnginePairWork"));
            });
        }

        [Test]
        public void UiShellTypes_DoNotStoreSyncEngineDependencies()
        {
            string mainWindowSource = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml.cs"));
            string viewModelSource = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(
                        Path.Combine(Path.GetDirectoryName(GetDesktopFilePath("Cotton.Sync.Desktop.csproj"))!, "ViewModels"),
                        "ShellViewModel*.cs")
                    .Select(File.ReadAllText));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowSource, Does.Not.Contain("Cotton.Sync.SyncEngine"));
                Assert.That(mainWindowSource, Does.Not.Contain("SyncEnginePairWork"));
                Assert.That(viewModelSource, Does.Not.Contain("Cotton.Sync.SyncEngine"));
                Assert.That(viewModelSource, Does.Not.Contain("SyncEnginePairWork"));
            });
        }

        private static string GetDesktopFilePath(string relativePath)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(directory, "src", "Cotton.Sync.Desktop", relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
            }

            throw new FileNotFoundException(relativePath);
        }
    }
}
