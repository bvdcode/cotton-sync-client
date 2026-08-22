// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopSetupVisualContractTests
    {
        [Test]
        public void AddFolderWizard_UsesFolderSelectionPrimaryAction()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string addFolderWizard = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\"",
                "IsVisible=\"{Binding IsSettingsVisible}\"");

            Assert.Multiple(() =>
            {
                Assert.That(addFolderWizard, Does.Contain("Content=\"{Binding RemoteFolderWizardPrimaryActionText}\""));
                Assert.That(addFolderWizard, Does.Contain("ToolTip.Tip=\"{Binding RemoteFolderWizardPrimaryActionToolTip}\""));
                Assert.That(addFolderWizard, Does.Contain("UseRemoteFolderCommand"));
                Assert.That(addFolderWizard, Does.Contain("IsVisible=\"{Binding IsRemoteFolderLoadingVisible}\""));
                Assert.That(addFolderWizard, Does.Contain("Text=\"{Binding RemoteFolderLoadingMessage}\""));
                Assert.That(addFolderWizard, Does.Contain("IsAddSyncPairLocalSummaryVisible"));
                Assert.That(addFolderWizard, Does.Not.Contain("Command=\"{Binding AddSyncPairCommand}\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Content=\"Sync\""));
            });
        }

        [Test]
        public void AddFolderWizard_ExplainsSyncModeDiskSpaceBehavior()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));
            string addFolderWizard = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\"",
                "IsVisible=\"{Binding IsSettingsVisible}\"");

            Assert.Multiple(() =>
            {
                Assert.That(addFolderWizard, Does.Contain("GroupName=\"AddSyncPairMode\""));
                Assert.That(addFolderWizard, Does.Contain("IsFullMirrorSyncModeSelected"));
                Assert.That(addFolderWizard, Does.Contain("IsWindowsVirtualFilesSyncModeSelected"));
                Assert.That(addFolderWizard, Does.Contain("Classes=\"sync-mode-pill\""));
                Assert.That(addFolderWizard, Does.Contain("Content=\"Full\""));
                Assert.That(addFolderWizard, Does.Contain("Content=\"Virtual\""));
                Assert.That(addFolderWizard, Does.Contain("ToolTip.Tip=\"Full mirror: stores every file on this device.\""));
                Assert.That(addFolderWizard, Does.Contain("ToolTip.Tip=\"Virtual files: saves disk space; downloads on open.\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Classes=\"sync-mode-card\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Text=\"Stores every file on this device.\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Text=\"Saves disk space; downloads on open.\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Text=\"Available\""));
                Assert.That(addFolderWizard, Does.Not.Contain("Text=\"Not implemented\""));
                Assert.That(appXaml, Does.Contain("Style Selector=\"RadioButton.sync-mode-pill\""));
                Assert.That(appXaml, Does.Contain("Style Selector=\"RadioButton.sync-mode-pill:checked\""));
            });
        }

        [Test]
        public void AddFolderWizard_KeepsCloudModeAndActionVisibleInCompactHeight()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string cloudStep = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairCloudStepVisible}\"",
                "IsVisible=\"{Binding IsSettingsVisible}\"");

            Assert.Multiple(() =>
            {
                Assert.That(cloudStep, Does.Contain("MaxHeight=\"240\""));
                Assert.That(cloudStep, Does.Contain("MinHeight=\"132\""));
                Assert.That(cloudStep, Does.Not.Contain("Height=\"260\""));
                Assert.That(cloudStep, Does.Contain("PlaceholderText=\"Search cloud folders\""));
                Assert.That(cloudStep, Does.Contain("Orientation=\"Horizontal\""));
                Assert.That(cloudStep, Does.Contain("Content=\"Full\""));
                Assert.That(cloudStep, Does.Contain("Content=\"Virtual\""));
                Assert.That(cloudStep, Does.Contain("Content=\"{Binding RemoteFolderWizardPrimaryActionText}\""));
                Assert.That(cloudStep, Does.Contain("<Grid Grid.Row=\"3\""));
                Assert.That(cloudStep.IndexOf("</ScrollViewer>", StringComparison.Ordinal), Is.LessThan(
                    cloudStep.IndexOf("Content=\"{Binding RemoteFolderWizardPrimaryActionText}\"", StringComparison.Ordinal)));
                Assert.That(cloudStep.IndexOf("Content=\"Virtual\"", StringComparison.Ordinal), Is.LessThan(
                    cloudStep.IndexOf("Content=\"{Binding RemoteFolderWizardPrimaryActionText}\"", StringComparison.Ordinal)));
            });
        }
    }
}
