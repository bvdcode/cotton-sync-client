// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopSetupVisualContractTests
    {
        [Test]
        public void SettingsDiagnostics_ScrollsWholeTabWithoutNestedSelfTestScrolling()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string diagnosticsSection = GetSlice(
                mainWindowXaml,
                "<TabItem Header=\"Diagnostics\"",
                "</TabItem>");
            int selfTestIndex = diagnosticsSection.IndexOf(
                "ItemsSource=\"{Binding SelfTestItems}\"",
                StringComparison.Ordinal);
            int diagnosticsIndex = diagnosticsSection.IndexOf(
                "ItemsSource=\"{Binding DiagnosticsItems}\"",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(selfTestIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(diagnosticsIndex, Is.GreaterThan(selfTestIndex));
                Assert.That(diagnosticsSection, Does.Not.Contain("MaxHeight=\"118\""));
                Assert.That(diagnosticsSection, Does.Contain("<ScrollViewer Margin=\"0,10,0,0\""));
            });
        }

        [Test]
        public void SettingsOverlay_StretchesWithinDashboardWindow()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string settingsOverlay = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSettingsVisible}\"",
                "</Window>");

            Assert.Multiple(() =>
            {
                Assert.That(settingsOverlay, Does.Not.Contain("MaxWidth=\"372\""));
                Assert.That(settingsOverlay, Does.Contain("HorizontalAlignment=\"Stretch\""));
                Assert.That(settingsOverlay, Does.Contain("VerticalAlignment=\"Stretch\""));
                Assert.That(settingsOverlay, Does.Contain("RowDefinitions=\"Auto,*\""));
                Assert.That(settingsOverlay, Does.Contain("<TabControl Grid.Row=\"1\""));
                Assert.That(settingsOverlay, Does.Contain("Classes=\"settingsTabs\""));
                Assert.That(settingsOverlay, Does.Contain("KeyboardNavigation.TabNavigation=\"Continue\""));
                Assert.That(settingsOverlay, Does.Contain("SelectedIndex=\"{Binding SelectedSettingsTabIndex}\""));
                Assert.That(settingsOverlay, Does.Not.Contain("<Border Width=\"372\""));
                Assert.That(settingsOverlay, Does.Not.Contain("MaxHeight=\"432\""));
                Assert.That(settingsOverlay, Does.Not.Contain("<ScrollViewer Grid.Row=\"1\""));
            });
        }

        [Test]
        public void DashboardContent_KeepsFoldersPrimaryAndActivityCollapsible()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string dashboardView = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsDashboardVisible}\">",
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\">");

            Assert.Multiple(() =>
            {
                Assert.That(dashboardView, Does.Contain("<RowDefinition Height=\"Auto\" />"));
                Assert.That(dashboardView, Does.Contain("<RowDefinition Height=\"*\" MinHeight=\"132\" />"));
                Assert.That(dashboardView, Does.Contain("<Grid Grid.Row=\"1\""));
                Assert.That(dashboardView, Does.Contain("RowDefinitions=\"*,Auto\""));
                Assert.That(dashboardView, Does.Contain("<StackPanel Grid.Row=\"0\""));
                Assert.That(dashboardView, Does.Contain("IsVisible=\"{Binding IsDashboardChromeVisible}\""));
                Assert.That(dashboardView, Does.Contain("<Border Grid.Row=\"0\""));
                Assert.That(dashboardView, Does.Contain("<Border Grid.Row=\"1\""));
                Assert.That(dashboardView, Does.Contain("Padding=\"10\""));
                Assert.That(dashboardView, Does.Contain("MaxHeight=\"150\""));
                Assert.That(dashboardView, Does.Contain("IsVisible=\"{Binding IsActivityVisible}\""));
                Assert.That(dashboardView, Does.Contain("VerticalScrollBarVisibility=\"Auto\""));
                Assert.That(dashboardView, Does.Contain("Grid.RowSpan=\"2\""));
                Assert.That(dashboardView, Does.Not.Contain("<ScrollViewer Grid.Row=\"0\""));
                Assert.That(dashboardView, Does.Not.Contain("MaxHeight=\"332\""));
                Assert.That(dashboardView, Does.Not.Contain("MaxHeight=\"300\""));
                Assert.That(dashboardView, Does.Not.Contain("MaxHeight=\"320\""));
                Assert.That(dashboardView, Does.Not.Contain("<ScrollViewer MaxHeight=\"216\""));
                Assert.That(dashboardView, Does.Not.Contain("<ScrollViewer Margin=\"10\""));
                Assert.That(dashboardView, Does.Not.Contain("RowDefinitions=\"Auto,*,*\""));
                Assert.That(dashboardView, Does.Not.Contain("RowDefinitions=\"Auto,Auto,Auto,Auto,*\""));
                Assert.That(dashboardView, Does.Not.Contain("RowDefinitions=\"Auto,132\""));
                Assert.That(dashboardView, Does.Not.Contain("Height=\"Auto\" MaxHeight=\"236\""));
                Assert.That(dashboardView, Does.Not.Contain("Height=\"2*\""));
                Assert.That(dashboardView, Does.Not.Contain("<RowDefinition Height=\"*\" MinHeight=\"112\" />"));
            });
        }

        [Test]
        public void ActivityRows_UseShortEventKindAsTitle()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string activitySection = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Activity\"",
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\">");

            Assert.Multiple(() =>
            {
                Assert.That(activitySection, Does.Contain("Text=\"{Binding Kind}\""));
                Assert.That(activitySection, Does.Contain("FontWeight=\"SemiBold\""));
                Assert.That(activitySection, Does.Contain("Text=\"{Binding Details}\""));
                Assert.That(activitySection, Does.Contain("ToolTip.Tip=\"{Binding Details}\""));
                Assert.That(activitySection, Does.Contain("TextWrapping=\"NoWrap\""));
                Assert.That(activitySection, Does.Contain("MaxLines=\"1\""));
                Assert.That(activitySection, Does.Contain("Text=\"{Binding Path}\""));
                Assert.That(activitySection, Does.Contain("ToolTip.Tip=\"{Binding Path}\""));
                Assert.That(activitySection, Does.Contain("IsVisible=\"{Binding HasPath}\""));
            });
        }

        [Test]
        public void TruncatedDynamicValuesExposeFullValueTooltips()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding ServerUrl}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding ServerUrl}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding StatusCardDetailText}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding StatusCardDetailText}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding CurrentWorkProgressDetails}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding CurrentWorkProgressDetails}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding CurrentWorkProgressSecondaryDetails}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding CurrentWorkProgressSecondaryDetails}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding Message}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding Message}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding CurrentOperation}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding CurrentOperation}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding RemoteBrowserPath}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding RemoteBrowserPath}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding Details}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding Details}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding Value}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding Value}\""));
            });
        }

        [Test]
        public void CloseIconButtons_UseMaterialCloseIcon()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Not.Contain("Content=\"x\""));
                Assert.That(mainWindowXaml, Does.Not.Contain("Content=\"×\""));
                Assert.That(CountOccurrences(mainWindowXaml, "Kind=\"CloseCircleOutline\""), Is.EqualTo(3));
            });
        }

        [Test]
        public void MoreIconButtons_UseMaterialMenuIcon()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Not.Contain("Content=\"...\""));
                Assert.That(mainWindowXaml, Does.Not.Contain("Content=\"…\""));
                Assert.That(CountOccurrences(mainWindowXaml, "Kind=\"DotsVertical\""), Is.EqualTo(2));
            });
        }

        [Test]
        public void FoldersPanel_ProvidesHeaderAndEmptyStateAddActions()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string foldersPanel = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Folders\"",
                "<TextBlock Text=\"Activity\"");

            Assert.Multiple(() =>
            {
                Assert.That(CountOccurrences(foldersPanel, "Command=\"{Binding ShowAddSyncPairCommand}\""), Is.EqualTo(2));
                Assert.That(foldersPanel, Does.Contain("ToolTip.Tip=\"Add sync folder\""));
                Assert.That(foldersPanel, Does.Contain("Text=\"No folders yet\""));
                Assert.That(foldersPanel, Does.Contain("Text=\"You can add more folders later.\""));
                Assert.That(foldersPanel, Does.Not.Contain("Text=\"Add a folder\""));
            });
        }

        [Test]
        public void SettingsOverlay_UsesTabbedSections()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string settingsOverlay = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSettingsVisible}\"",
                "</Window>");

            Assert.Multiple(() =>
            {
                Assert.That(settingsOverlay, Does.Contain("Text=\"Manage account, sync behavior, and diagnostics.\""));
                Assert.That(settingsOverlay, Does.Not.Contain("Text=\"Account, startup, preferences, diagnostics\""));
                Assert.That(settingsOverlay, Does.Not.Contain("Text=\"Account, startup, and diagnostics\""));
                Assert.That(settingsOverlay, Does.Contain("<TabItem Header=\"Account\""));
                Assert.That(settingsOverlay, Does.Not.Contain("<TabItem Header=\"Startup\""));
                Assert.That(settingsOverlay, Does.Contain("<TabItem Header=\"Preferences\""));
                Assert.That(settingsOverlay, Does.Contain("<TabItem Header=\"Diagnostics\""));
                Assert.That(settingsOverlay, Does.Contain("AutomationProperties.Name=\"Account settings\""));
                Assert.That(settingsOverlay, Does.Not.Contain("AutomationProperties.Name=\"Startup settings\""));
                Assert.That(settingsOverlay, Does.Contain("AutomationProperties.Name=\"Preferences settings\""));
                Assert.That(settingsOverlay, Does.Contain("AutomationProperties.Name=\"Diagnostics settings\""));
                Assert.That(settingsOverlay, Does.Contain("TabIndex=\"0\""));
                Assert.That(settingsOverlay, Does.Contain("TabIndex=\"1\""));
                Assert.That(settingsOverlay, Does.Contain("TabIndex=\"2\""));
                Assert.That(settingsOverlay, Does.Not.Contain("TabIndex=\"3\""));
                Assert.That(CountOccurrences(settingsOverlay, "<TabItem Header="), Is.EqualTo(3));
                Assert.That(settingsOverlay, Does.Not.Contain("Header=\"Start\""));
                Assert.That(settingsOverlay, Does.Not.Contain("Header=\"Prefs\""));
                Assert.That(settingsOverlay, Does.Not.Contain("Header=\"Diag\""));
            });
        }

        [Test]
        public void SettingsPreferences_IncludesStartupControls()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string preferencesSection = GetSlice(
                mainWindowXaml,
                "<TabItem Header=\"Preferences\"",
                "</TabItem>");

            Assert.Multiple(() =>
            {
                Assert.That(preferencesSection, Does.Contain("Text=\"Appearance\""));
                Assert.That(preferencesSection, Does.Contain("Text=\"Notifications\""));
                Assert.That(preferencesSection, Does.Contain("Text=\"Startup\""));
                Assert.That(preferencesSection, Does.Contain("Content=\"Launch on startup\""));
                Assert.That(preferencesSection, Does.Contain("IsVisible=\"{Binding IsStartWithOperatingSystemSupported}\""));
                Assert.That(preferencesSection, Does.Not.Contain("IsEnabled=\"{Binding IsStartWithOperatingSystemSupported}\""));
                Assert.That(preferencesSection, Does.Contain("Text=\"{Binding AutostartStatusText}\""));
            });
        }

        [Test]
        public void SettingsAccountTab_IncludesAboutSectionWithoutAddingExtraTab()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string settingsOverlay = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSettingsVisible}\"",
                "</Window>");
            string accountTab = GetSlice(
                settingsOverlay,
                "<TabItem Header=\"Account\"",
                "<TabItem Header=\"Preferences\"");

            Assert.Multiple(() =>
            {
                Assert.That(accountTab, Does.Contain("Text=\"About\""));
                Assert.That(accountTab, Does.Contain("Text=\"{Binding AppVersion}\""));
                Assert.That(accountTab, Does.Contain("Text=\"Device name\""));
                Assert.That(accountTab, Does.Contain("Text=\"{Binding DeviceName}\""));
                Assert.That(accountTab, Does.Not.Contain("Text=\"Cotton Sync Desktop\""));
                Assert.That(CountOccurrences(settingsOverlay, "<TabItem Header="), Is.EqualTo(3));
            });
        }

        [Test]
        public void DashboardActionRows_LabelRepairActionsAndKeepNarrowHeaderActionsIconOnly()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string dashboardHeader = GetSlice(
                mainWindowXaml,
                "Text=\"{Binding HeaderTitleText}\"",
                "<Grid Grid.Row=\"2\"");
            string actionRequiredRow = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Action required\"",
                "<Border MaxHeight=\"94\"");
            string conflictsHeader = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Conflicts\"",
                "<ScrollViewer Grid.Row=\"1\"");
            string conflictsSection = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Conflicts\"",
                "<TextBlock Text=\"Folders\"");
            string conflictDetails = GetSlice(
                conflictsSection,
                "Text=\"{Binding Details}\"",
                "<Button Grid.Column=\"2\"");

            Assert.Multiple(() =>
            {
                Assert.That(dashboardHeader, Does.Contain("Text=\"{Binding HeaderTitleText}\""));
                Assert.That(dashboardHeader, Does.Not.Contain("<TextBlock Text=\"Cotton Sync\""));
                Assert.That(dashboardHeader, Does.Contain("ToolTip.Tip=\"Sync now\""));
                Assert.That(dashboardHeader, Does.Contain("Kind=\"Refresh\""));
                Assert.That(dashboardHeader, Does.Contain("IsVisible=\"{Binding CanSyncNow}\""));
                Assert.That(dashboardHeader, Does.Contain("Header=\"Open in web\""));
                Assert.That(dashboardHeader, Does.Contain("<Separator />"));
                Assert.That(dashboardHeader, Does.Contain("Header=\"Sign out\""));
                Assert.That(dashboardHeader, Does.Contain("Classes=\"dangerMenuItem\""));
                Assert.That(dashboardHeader, Does.Not.Contain("Header=\"Web app\""));
                Assert.That(dashboardHeader, Does.Not.Contain("Header=\"Open in Cotton Cloud\""));
                Assert.That(dashboardHeader, Does.Not.Contain("Header=\"Open Cotton Cloud\""));
                Assert.That(dashboardHeader, Does.Not.Contain("Content=\"Sync\""));
                Assert.That(actionRequiredRow, Does.Contain("Kind=\"Refresh\""));
                Assert.That(actionRequiredRow, Does.Contain("Kind=\"CheckCircleOutline\""));
                Assert.That(actionRequiredRow, Does.Contain("Grid.Row=\"1\""));
                Assert.That(actionRequiredRow, Does.Contain("HorizontalAlignment=\"Right\""));
                Assert.That(actionRequiredRow, Does.Contain("MaxLines=\"4\""));
                Assert.That(actionRequiredRow, Does.Contain("TextWrapping=\"Wrap\""));
                Assert.That(actionRequiredRow, Does.Contain("ToolTip.Tip=\"{Binding ActionRequiredMessage}\""));
                Assert.That(actionRequiredRow, Does.Contain("Text=\"Retry\""));
                Assert.That(actionRequiredRow, Does.Contain("Text=\"Diagnostics\""));
                Assert.That(actionRequiredRow, Does.Not.Contain("Content=\"Retry\""));
                Assert.That(actionRequiredRow, Does.Not.Contain("Content=\"Check\""));
                Assert.That(conflictsHeader, Does.Contain("Kind=\"Refresh\""));
                Assert.That(conflictsHeader, Does.Not.Contain("OpenConflictCommand"));
                Assert.That(conflictsHeader, Does.Not.Contain("Open selected conflict location"));
                Assert.That(conflictsSection, Does.Contain("<ItemsControl ItemsSource=\"{Binding Conflicts}\""));
                Assert.That(conflictsSection, Does.Not.Contain("SelectedItem=\"{Binding SelectedConflict}\""));
                Assert.That(conflictsSection, Does.Contain("OpenConflictCommand"));
                Assert.That(conflictsSection, Does.Contain("CommandParameter=\"{Binding}\""));
                Assert.That(conflictsSection, Does.Contain("ToolTip.Tip=\"Open conflict location\""));
                Assert.That(conflictsSection, Does.Contain("Kind=\"ArrowTopRight\""));
                Assert.That(conflictDetails, Does.Contain("TextWrapping=\"Wrap\""));
                Assert.That(conflictDetails, Does.Contain("MaxLines=\"3\""));
                Assert.That(conflictDetails, Does.Not.Contain("TextTrimming=\"CharacterEllipsis\""));
                Assert.That(conflictsHeader, Does.Not.Contain("Content=\"Retry\""));
                Assert.That(conflictsHeader, Does.Not.Contain("Content=\"Open\""));
            });
        }
    }
}
