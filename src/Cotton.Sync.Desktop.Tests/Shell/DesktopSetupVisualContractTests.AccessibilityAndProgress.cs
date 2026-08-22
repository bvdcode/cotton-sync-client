// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopSetupVisualContractTests
    {
        [Test]
        public void IconButtons_WithTooltipsExposeAutomationNames()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            IReadOnlyList<string> missingAutomationNames = FindIconButtonTooltipsWithoutAutomationName(mainWindowXaml);

            Assert.Multiple(() =>
            {
                Assert.That(missingAutomationNames, Is.Empty, string.Join(Environment.NewLine, missingAutomationNames));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"Sync now\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"Sync now\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding ActivityToggleToolTip}\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"{Binding ActivityToggleToolTip}\""));
                Assert.That(mainWindowXaml, Does.Contain("ToolTip.Tip=\"{Binding ToggleEnabledLabel}\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"{Binding ToggleEnabledLabel}\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"Open conflict location\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"Go to parent folder\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"Create cloud folder\""));
                Assert.That(mainWindowXaml, Does.Contain("AutomationProperties.Name=\"Close\""));
            });
        }

        [Test]
        public void AddFolderWizard_WrapsActionRequiredMessage()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string wizardError = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\"",
                "<ScrollViewer Grid.Row=\"2\"");
            wizardError = GetSlice(
                wizardError,
                "ToolTip.Tip=\"{Binding ActionRequiredMessage}\"",
                "</Border>");

            Assert.Multiple(() =>
            {
                Assert.That(wizardError, Does.Contain("Text=\"{Binding ActionRequiredMessage}\""));
                Assert.That(wizardError, Does.Contain("MaxLines=\"3\""));
                Assert.That(wizardError, Does.Contain("TextWrapping=\"Wrap\""));
                Assert.That(wizardError, Does.Contain("TextTrimming=\"CharacterEllipsis\""));
            });
        }

        [Test]
        public void StatusCard_UsesAttentionStateForActionRequired()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string statusCard = GetSlice(
                mainWindowXaml,
                "Classes=\"statusCard\"",
                "<TextBlock Text=\"Action required\"");

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding HeaderTitleText}\""));
                Assert.That(mainWindowXaml, Does.Contain("Text=\"{Binding HeaderStatusText}\""));
                Assert.That(mainWindowXaml, Does.Not.Contain("Text=\"{Binding GlobalStatus}\""));
                Assert.That(statusCard, Does.Contain("IsVisible=\"{Binding IsStatusCardVisible}\""));
                Assert.That(statusCard, Does.Contain("Classes.actionRequired=\"{Binding HasStatusAttention}\""));
                Assert.That(statusCard, Does.Contain("Classes.offline=\"{Binding HasOfflineStatus}\""));
                Assert.That(statusCard, Does.Contain("Classes.waiting=\"{Binding HasWaitingStatus}\""));
                Assert.That(statusCard, Does.Not.Contain("Classes.actionRequired=\"{Binding HasActionRequired}\""));
                Assert.That(statusCard, Does.Contain("Text=\"{Binding StatusCardDetailText}\""));
                Assert.That(statusCard, Does.Contain("IsVisible=\"{Binding HasStatusCardDetail}\""));
                Assert.That(statusCard, Does.Not.Contain("Text=\"{Binding AccountName}\""));
                Assert.That(statusCard, Does.Not.Contain("Text=\"{Binding CurrentProgressText}\""));
            });
        }

        [Test]
        public void DashboardProgressCards_ExposeRunAndTransferProgress()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string dashboardView = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsDashboardVisible}\">",
                "<TextBlock Text=\"Action required\"");

            Assert.Multiple(() =>
            {
                Assert.That(dashboardView, Does.Contain("IsVisible=\"{Binding HasCurrentWorkProgress}\""));
                Assert.That(dashboardView, Does.Contain("ColumnDefinitions=\"*,Auto\""));
                Assert.That(dashboardView, Does.Contain("Text=\"{Binding CurrentWorkProgressTitle}\""));
                Assert.That(dashboardView, Does.Not.Contain("Text=\"{Binding CurrentWorkProgressHeaderDetails}\""));
                Assert.That(dashboardView, Does.Contain("Text=\"{Binding CurrentWorkProgressHeaderSizeDetails}\""));
                Assert.That(dashboardView, Does.Contain("Text=\"{Binding CurrentWorkProgressHeaderRateDetails}\""));
                Assert.That(dashboardView, Does.Contain("HorizontalAlignment=\"Right\""));
                Assert.That(dashboardView, Does.Contain("MinHeight=\"15\""));
                Assert.That(dashboardView, Does.Contain("Text=\"{Binding CurrentWorkProgressDetails}\""));
                Assert.That(dashboardView, Does.Contain("Text=\"{Binding CurrentWorkProgressSecondaryDetails}\""));
                Assert.That(dashboardView, Does.Contain("MinHeight=\"16\""));
                Assert.That(dashboardView, Does.Contain("Value=\"{Binding CurrentWorkProgressValue}\""));
                Assert.That(dashboardView, Does.Contain("IsIndeterminate=\"{Binding IsCurrentWorkProgressIndeterminate}\""));
                Assert.That(dashboardView, Does.Not.Contain("IsVisible=\"{Binding HasCurrentRunProgress}\""));
                Assert.That(dashboardView, Does.Not.Contain("IsVisible=\"{Binding HasCurrentTransfer}\""));
            });
        }

        [Test]
        public void DashboardNotifications_UseDashboardVisibilityGate()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string notificationsView = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding HasDashboardNotifications}\"",
                "IsVisible=\"{Binding HasConflicts}\"");

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Contain("IsVisible=\"{Binding HasDashboardNotifications}\""));
                Assert.That(notificationsView, Does.Contain("IsVisible=\"{Binding IsDashboardVisible}\""));
                Assert.That(mainWindowXaml, Does.Not.Contain("IsVisible=\"{Binding HasNotifications}\""));
            });
        }

        [Test]
        public void DashboardLayout_BoundsFoldersAndActivityBelowStableStatusChrome()
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
                Assert.That(dashboardView, Does.Contain("IsVisible=\"{Binding IsActivityVisible}\""));
                Assert.That(dashboardView, Does.Not.Contain("Height=\"Auto\" MaxHeight=\"236\""));
                Assert.That(dashboardView, Does.Not.Contain("Height=\"2*\""));
                Assert.That(dashboardView, Does.Not.Contain("<RowDefinition Height=\"*\" MinHeight=\"112\" />"));
            });
        }

        [Test]
        public void SettingsDiagnostics_UsesClearDiagnosticsActions()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));
            string diagnosticsSection = GetSlice(
                mainWindowXaml,
                "<TabItem Header=\"Diagnostics\"",
                "</TabItem>");

            Assert.Multiple(() =>
            {
                Assert.That(diagnosticsSection, Does.Contain("Content=\"Run checks\""));
                Assert.That(diagnosticsSection, Does.Contain("Content=\"Export logs\""));
                Assert.That(diagnosticsSection, Does.Contain("Content=\"Open data\""));
                Assert.That(diagnosticsSection, Does.Not.Contain("Content=\"Export diagnostics\""));
                Assert.That(diagnosticsSection, Does.Not.Contain("Content=\"Export bundle\""));
                Assert.That(diagnosticsSection, Does.Contain("ToolTip.Tip=\"Export logs and diagnostic state\""));
                Assert.That(diagnosticsSection, Does.Contain("ToolTip.Tip=\"Open app data folder\""));
                Assert.That(diagnosticsSection, Does.Contain("Text=\"Logs exported to\""));
                Assert.That(diagnosticsSection, Does.Contain("IsVisible=\"{Binding HasDataDirectory}\""));
                Assert.That(diagnosticsSection, Does.Contain("Classes=\"selfTestResult\""));
                Assert.That(diagnosticsSection, Does.Contain("Classes.passed=\"{Binding Passed}\""));
                Assert.That(diagnosticsSection, Does.Contain("Classes.failed=\"{Binding IsFailed}\""));
                Assert.That(appXaml, Does.Contain("TextBlock.selfTestResult.passed"));
                Assert.That(appXaml, Does.Contain("TextBlock.selfTestResult.failed"));
                Assert.That(diagnosticsSection, Does.Contain("OpenDataFolderCommand"));
                Assert.That(diagnosticsSection, Does.Contain("LastDiagnosticsBundlePath"));
                Assert.That(diagnosticsSection, Does.Contain("OpenDiagnosticsBundleFolderCommand"));
            });
        }

        [Test]
        public void SettingsDiagnostics_ShowsFlatSelfTestDetailsWithoutFalseDropdowns()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string diagnosticsSection = GetSlice(
                mainWindowXaml,
                "<TabItem Header=\"Diagnostics\"",
                "</TabItem>");

            Assert.Multiple(() =>
            {
                Assert.That(diagnosticsSection, Does.Not.Contain("<Expander"));
                Assert.That(diagnosticsSection, Does.Not.Contain("AreDetailsExpanded"));
                Assert.That(diagnosticsSection, Does.Not.Contain("AutomationProperties.Name=\"Toggle diagnostic details\""));
                Assert.That(diagnosticsSection, Does.Not.Contain("<Expander.Header>"));
                Assert.That(diagnosticsSection, Does.Contain("Grid.Row=\"1\""));
                Assert.That(diagnosticsSection, Does.Contain("Text=\"{Binding Details}\""));
                Assert.That(diagnosticsSection, Does.Contain("IsVisible=\"{Binding HasDetails}\""));
                Assert.That(diagnosticsSection, Does.Contain("TextWrapping=\"Wrap\""));
            });
        }

        [Test]
        public void AddFolderWizard_StretchesWithinDashboardWindow()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string addFolderWizard = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairWizardVisible}\"",
                "IsVisible=\"{Binding IsSettingsVisible}\"");

            Assert.Multiple(() =>
            {
                Assert.That(addFolderWizard, Does.Not.Contain("MaxWidth=\"372\""));
                Assert.That(addFolderWizard, Does.Contain("HorizontalAlignment=\"Stretch\""));
                Assert.That(addFolderWizard, Does.Contain("VerticalAlignment=\"Stretch\""));
                Assert.That(addFolderWizard, Does.Contain("ClipToBounds=\"True\""));
                Assert.That(addFolderWizard, Does.Contain("<Grid RowDefinitions=\"Auto,Auto,*,Auto\""));
                Assert.That(addFolderWizard, Does.Contain("<ScrollViewer Grid.Row=\"2\""));
                Assert.That(addFolderWizard, Does.Contain("<Grid Grid.Row=\"3\""));
                Assert.That(addFolderWizard, Does.Contain("VerticalScrollBarVisibility=\"Auto\""));
                Assert.That(addFolderWizard, Does.Contain("ToolTip.Tip=\"{Binding ActionRequiredMessage}\""));
                Assert.That(addFolderWizard, Does.Not.Contain("<Border Width=\"372\""));
            });
        }

        [Test]
        public void DashboardOverlays_KeepKeyboardFocusInsideTheActiveSurface()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(CountOccurrences(mainWindowXaml, "KeyboardNavigation.TabNavigation=\"Cycle\""), Is.EqualTo(1));
                Assert.That(CountOccurrences(mainWindowXaml, "IsEnabled=\"{Binding IsDashboardChromeVisible}\""), Is.EqualTo(2));
                Assert.That(mainWindowXaml, Does.Contain("x:Name=\"CancelAddSyncPairButton\""));
                Assert.That(mainWindowXaml, Does.Contain("x:Name=\"CloseSettingsButton\""));
                Assert.That(mainWindowXaml, Does.Contain("x:Name=\"SettingsTabControl\""));
            });
        }

        [Test]
        public void MainWindow_FocusesAndClosesTheActiveDashboardOverlayFromKeyboard()
        {
            string mainWindowCode = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowCode, Does.Contain("FocusOverlayAction(viewModel.IsAddSyncPairWizardVisible, CancelAddSyncPairButton);"));
                Assert.That(mainWindowCode, Does.Contain("FocusOverlayAction(viewModel.IsSettingsVisible, CloseSettingsButton);"));
                Assert.That(mainWindowCode, Does.Contain("protected override void OnKeyDown(KeyEventArgs e)"));
                Assert.That(mainWindowCode, Does.Contain("TryCycleSettingsFocus(e)"));
                Assert.That(mainWindowCode, Does.Contain("SettingsTabControl.SelectedItem is Control selectedTab"));
                Assert.That(mainWindowCode, Does.Contain("!CloseSettingsButton.IsKeyboardFocusWithin"));
                Assert.That(mainWindowCode, Does.Contain("CancelCreateRemoteFolderCommand.Execute(null);"));
                Assert.That(mainWindowCode, Does.Contain("CancelAddSyncPairCommand.Execute(null);"));
                Assert.That(mainWindowCode, Does.Contain("CloseSettingsCommand.Execute(null);"));
            });
        }
    }
}
