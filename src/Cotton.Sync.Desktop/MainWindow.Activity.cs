// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop
{
    public partial class MainWindow
    {
        private const double ActivitySplitterHeight = 8;
        private const double MinimumActivityHeight = 80;
        private GridLength _activityPanelHeight = new(1, GridUnitType.Star);

        private void UpdateActivityPanel(bool isVisible)
        {
            RowDefinition splitterRow = FoldersAndActivityGrid.RowDefinitions[1];
            RowDefinition contentRow = FoldersAndActivityGrid.RowDefinitions[2];
            if (isVisible)
            {
                splitterRow.Height = new GridLength(ActivitySplitterHeight);
                contentRow.MinHeight = MinimumActivityHeight;
                contentRow.Height = _activityPanelHeight;
                return;
            }

            _activityPanelHeight = contentRow.Height;
            contentRow.MinHeight = 0;
            contentRow.Height = new GridLength(0);
            splitterRow.Height = new GridLength(0);
        }

        private async void CopyActivity_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ActivityRowViewModel activity })
            {
                return;
            }

            try
            {
                if (Clipboard is null)
                {
                    throw new InvalidOperationException("The system clipboard is unavailable.");
                }

                await Clipboard.SetTextAsync(activity.CopyText);
            }
            catch (Exception exception)
            {
                _viewModel.ReportCommandError(exception);
            }
        }
    }
}
