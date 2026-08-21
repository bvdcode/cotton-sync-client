// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private async Task ApplyStartWithOperatingSystemAsync(bool enabled)
        {
            if (_isApplyingStartWithOperatingSystem)
            {
                return;
            }

            _isApplyingStartWithOperatingSystem = true;
            IsBusy = true;
            try
            {
                await _controller.SetStartWithOperatingSystemAsync(enabled).ConfigureAwait(true);
                AddActivity("App", string.Empty, enabled ? "Start with computer enabled" : "Start with computer disabled");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _isLoadingSnapshot = true;
                StartWithOperatingSystem = !enabled;
                _isLoadingSnapshot = false;
                HandleCommandError(exception);
            }
            finally
            {
                _isApplyingStartWithOperatingSystem = false;
                IsBusy = false;
            }
        }

        private async Task ApplyNotificationsEnabledAsync(bool enabled)
        {
            if (_isApplyingNotificationPreference)
            {
                return;
            }

            _isApplyingNotificationPreference = true;
            try
            {
                await _controller.SetNotificationsEnabledAsync(enabled).ConfigureAwait(true);
                AddActivity("Settings", string.Empty, enabled ? "Desktop notifications enabled" : "Desktop notifications disabled");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _isLoadingSnapshot = true;
                EnableNotifications = !enabled;
                _isLoadingSnapshot = false;
                HandleCommandError(exception);
            }
            finally
            {
                _isApplyingNotificationPreference = false;
            }
        }

        private async Task ApplyThemeModeAsync(AppThemeMode themeMode, AppThemeMode previousThemeMode)
        {
            if (_isApplyingThemePreference)
            {
                return;
            }

            _isApplyingThemePreference = true;
            try
            {
                await _controller.SetThemeModeAsync(themeMode).ConfigureAwait(true);
                AddActivity("Settings", string.Empty, "Theme set to " + ThemeModeLabel);
                RefreshDiagnosticsItems();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _themeMode = previousThemeMode;
                OnPropertyChanged(nameof(ThemeModeIndex));
                OnPropertyChanged(nameof(ThemeModeLabel));
                _themeService.Apply(previousThemeMode);
                HandleCommandError(exception);
            }
            finally
            {
                _isApplyingThemePreference = false;
            }
        }
    }
}
