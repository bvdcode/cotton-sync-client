// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopSetupVisualContractTests
    {
        [Test]
        public void FoldersHeader_HasSingleCompactAddFolderCommand()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string foldersHeader = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Folders\"",
                "<Grid Grid.Row=\"1\"");

            Assert.Multiple(() =>
            {
                Assert.That(foldersHeader, Does.Not.Contain("Sync roots"));
                Assert.That(foldersHeader, Does.Contain("ToggleActivityCommand"));
                Assert.That(foldersHeader, Does.Contain("ToolTip.Tip=\"{Binding ActivityToggleToolTip}\""));
                Assert.That(foldersHeader, Does.Contain("Kind=\"History\""));
                Assert.That(foldersHeader, Does.Contain("ShowAddSyncPairCommand"));
                Assert.That(foldersHeader, Does.Contain("ToolTip.Tip=\"Add sync folder\""));
                Assert.That(foldersHeader, Does.Not.Contain("IsVisible=\"{Binding HasSyncPairs}\""));
                Assert.That(foldersHeader, Does.Contain("Classes=\"icon primary\""));
            });
        }

        [Test]
        public void EmptyFoldersState_ProvidesPrimaryAddFolderCommand()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string emptyFoldersState = GetSlice(
                mainWindowXaml,
                "MinHeight=\"166\"",
                "<ScrollViewer x:Name=\"SyncPairsScrollViewer\"");

            Assert.Multiple(() =>
            {
                Assert.That(CountOccurrences(emptyFoldersState, "ShowAddSyncPairCommand"), Is.EqualTo(1));
                Assert.That(CountOccurrences(emptyFoldersState, "Content=\"+\""), Is.Zero);
                Assert.That(emptyFoldersState, Does.Contain("Text=\"No folders yet\""));
                Assert.That(emptyFoldersState, Does.Contain("Text=\"Add sync folder\""));
                Assert.That(emptyFoldersState, Does.Contain("Text=\"Choose a local folder and where it syncs in Cotton Cloud.\""));
                Assert.That(emptyFoldersState, Does.Contain("MinHeight=\"166\""));
                Assert.That(emptyFoldersState, Does.Contain("VerticalAlignment=\"Center\""));
                Assert.That(emptyFoldersState, Does.Contain("Classes=\"primary\""));
                Assert.That(emptyFoldersState, Does.Not.Contain("<TextBlock Text=\"+\""));
            });
        }

        [Test]
        public void DashboardFolders_ExposeSelectedPairManagementActions()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));
            string foldersSection = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Folders\"",
                "<TextBlock Text=\"Activity\"");

            Assert.Multiple(() =>
            {
                Assert.That(foldersSection, Does.Contain("ShowSelectedSyncPairEditorCommand"));
                Assert.That(foldersSection, Does.Contain("CommandParameter=\"{Binding}\""));
                Assert.That(foldersSection, Does.Contain("<ItemsControl ItemsSource=\"{Binding SyncPairs}\">"));
                Assert.That(foldersSection, Does.Not.Contain("SelectedItem=\"{Binding SelectedSyncPair}\""));
                Assert.That(foldersSection, Does.Contain("IsVisible=\"{Binding IsEditorVisible}\""));
                Assert.That(foldersSection, Does.Not.Contain("IsVisible=\"{Binding IsSelectedSyncPairEditorVisible}\""));
                Assert.That(foldersSection, Does.Contain("Text=\"{Binding DisplayName}\""));
                Assert.That(foldersSection, Does.Contain("Classes=\"syncPairStatusIndicator\""));
                Assert.That(foldersSection, Does.Contain("Classes.active=\"{Binding IsStatusActive}\""));
                Assert.That(foldersSection, Does.Contain("Classes.paused=\"{Binding IsStatusPaused}\""));
                Assert.That(foldersSection, Does.Contain("Classes.offline=\"{Binding IsStatusOffline}\""));
                Assert.That(foldersSection, Does.Contain("Classes.waiting=\"{Binding IsStatusWaiting}\""));
                Assert.That(foldersSection, Does.Contain("Classes.attention=\"{Binding IsStatusAttention}\""));
                Assert.That(foldersSection, Does.Contain("ToolTip.Tip=\"{Binding StatusIndicatorToolTip}\""));
                Assert.That(foldersSection, Does.Contain("IsVisible=\"{Binding IsStatusIndicatorVisible}\""));
                Assert.That(foldersSection, Does.Not.Contain("Text=\"{Binding HeaderText}\""));
                Assert.That(foldersSection, Does.Not.Contain("Classes.errorStatus=\"{Binding IsErrorStatus}\""));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator"));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator.active"));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator.paused"));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator.offline"));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator.waiting"));
                Assert.That(appXaml, Does.Contain("Border.syncPairStatusIndicator.attention"));
                Assert.That(foldersSection, Does.Contain("Text=\"{Binding EditableDisplayName}\""));
                Assert.That(foldersSection, Does.Not.Contain("SelectedSyncPairEditableDisplayName"));
                Assert.That(foldersSection, Does.Contain("SaveSelectedSyncPairNameCommand"));
                Assert.That(foldersSection, Does.Contain("ToggleSelectedSyncPairEnabledCommand"));
                Assert.That(foldersSection, Does.Not.Contain("ChangeSelectedSyncPairLocalFolderCommand"));
                Assert.That(foldersSection, Does.Not.Contain("ChangeSelectedSyncPairRemoteFolderCommand"));
                Assert.That(foldersSection, Does.Contain("RemoveSelectedSyncPairCommand"));
                Assert.That(foldersSection, Does.Not.Contain("CancelSelectedSyncPairEditorCommand"));
                Assert.That(foldersSection, Does.Contain("IsRemoveSyncPairConfirmationVisible"));
                Assert.That(foldersSection, Does.Contain("IsRemoveSyncPairConfirmationActionsVisible"));
                Assert.That(foldersSection, Does.Contain("RemoveSyncPairConfirmationMessage"));
                Assert.That(foldersSection, Does.Contain("CancelRemoveSyncPairCommand"));
                Assert.That(foldersSection, Does.Contain("ConfirmRemoveSelectedSyncPairCommand"));
                Assert.That(foldersSection, Does.Contain("ToolTip.Tip=\"Rename or manage sync folder\""));
                Assert.That(foldersSection, Does.Contain("ToolTip.Tip=\"Open local folder\""));
                Assert.That(foldersSection, Does.Not.Contain("ToolTip.Tip=\"Change local folder\""));
                Assert.That(foldersSection, Does.Not.Contain("ToolTip.Tip=\"Change cloud folder\""));
                Assert.That(CountOccurrences(foldersSection, "Classes=\"inlineChange\""), Is.Zero);
                Assert.That(CountOccurrences(foldersSection, "ToolTip.Tip=\"Open local folder\""), Is.EqualTo(1));
                Assert.That(foldersSection, Does.Not.Contain("ToolTip.Tip=\"Open selected local folder\""));
                Assert.That(foldersSection, Does.Contain("ModeLabel"));
                Assert.That(foldersSection, Does.Not.Contain("SelectedSyncPair.ModeLabel"));
                Assert.That(foldersSection, Does.Contain("materialIcons:MaterialIcon"));
                Assert.That(foldersSection, Does.Contain("Kind=\"ContentSaveOutline\""));
                Assert.That(foldersSection, Does.Not.Contain("Kind=\"FolderSearchOutline\""));
                Assert.That(foldersSection, Does.Not.Contain("Kind=\"CloudSearchOutline\""));
                Assert.That(foldersSection, Does.Contain("Kind=\"FolderOffOutline\""));
                Assert.That(foldersSection, Does.Contain("Kind=\"FolderCheckOutline\""));
                Assert.That(foldersSection, Does.Not.Contain("Kind=\"PauseCircleOutline\""));
                Assert.That(foldersSection, Does.Not.Contain("Kind=\"PlayCircleOutline\""));
                Assert.That(foldersSection, Does.Contain("Kind=\"TrashCanOutline\""));
                Assert.That(foldersSection, Does.Contain("Text=\"Folder name\""));
                Assert.That(foldersSection, Does.Contain("Text=\"Save\""));
                Assert.That(foldersSection, Does.Not.Contain("Text=\"Local\""));
                Assert.That(foldersSection, Does.Not.Contain("Text=\"Cloud\""));
                Assert.That(foldersSection, Does.Contain("Text=\"{Binding ToggleEnabledShortLabel}\""));
                Assert.That(foldersSection, Does.Contain("Text=\"Remove\""));
                Assert.That(foldersSection, Does.Contain("Classes=\"compact danger\""));
                Assert.That(foldersSection, Does.Contain("Grid.Row=\"5\""));
                Assert.That(foldersSection, Does.Contain("RowDefinitions=\"Auto,Auto,Auto,Auto,Auto,Auto\""));
                Assert.That(foldersSection, Does.Not.Contain("<Path Data="));
                Assert.That(foldersSection, Does.Not.Contain("Content=\"{Binding ToggleEnabledIcon}\""));
                Assert.That(foldersSection, Does.Not.Contain("Content=\"💾\""));
                Assert.That(foldersSection, Does.Not.Contain("Content=\"🗑\""));
                Assert.That(foldersSection, Does.Not.Contain("Content=\"-\""));
                Assert.That(foldersSection, Does.Not.Contain("ToolTip.Tip=\"Close folder controls\""));
                Assert.That(foldersSection, Does.Contain("Text=\"{Binding CurrentOperation}\""));
                Assert.That(foldersSection, Does.Contain("IsVisible=\"{Binding HasCurrentOperation}\""));
                Assert.That(foldersSection, Does.Contain("Value=\"{Binding CurrentProgressValue}\""));
                Assert.That(foldersSection, Does.Contain("Margin=\"0,3,0,0\""));
                Assert.That(foldersSection, Does.Contain("IsIndeterminate=\"{Binding IsCurrentProgressIndeterminate}\""));
                Assert.That(foldersSection, Does.Contain("IsVisible=\"{Binding HasCurrentProgress}\""));
            });
        }

        [Test]
        public void DashboardFolders_TruncatesLongVirtualFilesLabelsAndPaths()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string foldersSection = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Folders\"",
                "<TextBlock Text=\"Activity\"");
            string modeAndLocalPathRow = GetSlice(
                foldersSection,
                "<Grid Grid.Row=\"3\"",
                "<TextBlock Grid.Row=\"4\"");
            string remotePathRow = GetSlice(
                foldersSection,
                "<TextBlock Grid.Row=\"4\"",
                "<Button Grid.Column=\"1\"");

            Assert.Multiple(() =>
            {
                Assert.That(modeAndLocalPathRow, Does.Contain("ColumnDefinitions=\"Auto,*\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("Kind=\"CloudOutline\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("IsVisible=\"{Binding IsWindowsVirtualFilesMode}\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("Kind=\"FolderOpenOutline\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("IsVisible=\"{Binding IsFullMirrorMode}\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("ToolTip.Tip=\"{Binding ModeLabel}\""));
                Assert.That(modeAndLocalPathRow, Does.Not.Contain("Text=\"{Binding ModeLabel}\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("Text=\"{Binding LocalPath}\""));
                Assert.That(modeAndLocalPathRow, Does.Contain("ToolTip.Tip=\"{Binding LocalPath}\""));
                Assert.That(CountOccurrences(modeAndLocalPathRow, "TextTrimming=\"CharacterEllipsis\""), Is.EqualTo(1));
                Assert.That(remotePathRow, Does.Contain("Text=\"{Binding RemotePathLabel}\""));
                Assert.That(remotePathRow, Does.Contain("ToolTip.Tip=\"{Binding RemotePathLabel}\""));
                Assert.That(remotePathRow, Does.Contain("IsVisible=\"{Binding HasRemotePathLabel}\""));
                Assert.That(remotePathRow, Does.Contain("TextTrimming=\"CharacterEllipsis\""));
            });
        }

        [Test]
        public void DashboardFolders_LeavesRoomForExpandedInlineControls()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string foldersSection = GetSlice(
                mainWindowXaml,
                "<TextBlock Text=\"Folders\"",
                "<TextBlock Text=\"Activity\"");

            Assert.Multiple(() =>
            {
                Assert.That(foldersSection, Does.Not.Contain("<ScrollViewer MaxHeight=\"216\""));
                Assert.That(foldersSection, Does.Not.Contain("<ScrollViewer MaxHeight=\"236\""));
                Assert.That(foldersSection, Does.Not.Contain("MaxHeight=\"300\""));
                Assert.That(foldersSection, Does.Contain("<ScrollViewer x:Name=\"SyncPairsScrollViewer\""));
                Assert.That(foldersSection, Does.Contain("VerticalScrollBarVisibility=\"Auto\""));
                Assert.That(foldersSection, Does.Contain("ClipToBounds=\"True\""));
                Assert.That(foldersSection, Does.Contain("MinHeight=\"0\""));
                Assert.That(foldersSection, Does.Contain("Tag=\"{Binding Id}\""));
                Assert.That(foldersSection, Does.Contain("<ItemsControl ItemsSource=\"{Binding SyncPairs}\">"));
            });
        }
    }
}
