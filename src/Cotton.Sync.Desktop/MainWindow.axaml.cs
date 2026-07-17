// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop
{
    /// <summary>
    /// Main desktop synchronization shell window.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double DashboardHeight = 540;
        private const double DashboardMinHeight = 520;
        private const double DashboardMinWidth = 388;
        private const double DashboardWidth = 400;
        private const double SetupServerHeight = 288;
        private const double SetupServerMinHeight = 280;
        private const double SetupSignInHeight = 452;
        private const double SetupSignInMinHeight = 440;
        private const double SetupMinWidth = 316;
        private const double SetupWidth = 336;
        private const double WindowFrameHeightAllowance = 48;

        private readonly ILogger _logger;
        private readonly DesktopWindowLifecyclePolicy _lifecyclePolicy;
        private readonly double? _visualSmokeScale;
        private readonly DesktopVisualSmokeScenario? _visualSmokeScenario;
        private readonly ShellViewModel _viewModel;
        private bool _hasOpened;
        private bool _hasInitializedShell;
        private bool _hasStartedShutdown;
        private WindowProfile? _windowProfile;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow" /> class.
        /// </summary>
        public MainWindow()
            : this(DesktopShellController.CreateDefault(), false, false)
        {
        }

        internal MainWindow(
            IDesktopShellController controller,
            bool startMinimizedToTray = false,
            bool canHideToTray = false,
            DesktopVisualSmokeScenario? visualSmokeScenario = null,
            double? visualSmokeScale = null)
        {
            ArgumentNullException.ThrowIfNull(controller);
            _logger = new DesktopTraceLoggerFactory().CreateLogger(nameof(MainWindow));
            _lifecyclePolicy = new DesktopWindowLifecyclePolicy(startMinimizedToTray, canHideToTray);
            _visualSmokeScenario = visualSmokeScenario;
            _visualSmokeScale = visualSmokeScenario is null ? null : visualSmokeScale;
            InitializeComponent();
            bool notifyOnSessionRestore = !startMinimizedToTray && visualSmokeScenario is null;
            _viewModel = new ShellViewModel(
                controller,
                new WindowLocalFolderPicker(this),
                DesktopNotificationServiceFactory.CreateDefault(),
                new AvaloniaDesktopThemeService(),
                checkForUpdatesOnStartup: visualSmokeScenario is null,
                notifyOnSessionRestore: notifyOnSessionRestore);
            DataContext = _viewModel;
            _viewModel.UpdateInstallShutdownRequested += OnUpdateInstallShutdownRequested;
            ApplyWindowMode(_viewModel);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Opened += async (_, _) =>
            {
                _hasOpened = true;
                FitAndCenterOnCurrentScreen();
                if (_lifecyclePolicy.ShouldHideAfterStartup())
                {
                    HideForTrayStartup();
                }

                await InitializeShellOnceAsync(_viewModel).ConfigureAwait(true);
                if (_lifecyclePolicy.ShouldHideAfterStartup())
                {
                    HideForTrayStartup();
                }
            };
            if (_visualSmokeScale is null)
            {
                ScalingChanged += (_, _) => Dispatcher.UIThread.Post(
                    FitAndCenterOnCurrentScreen,
                    DispatcherPriority.Loaded);
            }
            Closing += OnClosing;
            Closed += OnClosed;
        }

        internal void RequestQuit()
        {
            _lifecyclePolicy.RequestQuit();
        }

        internal void ShowShell()
        {
            _lifecyclePolicy.RequestShow();
            ShowInTaskbar = true;
            Opacity = 1;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();
        }

        internal void StartHiddenToTray()
        {
            HideForTrayStartup();
            Dispatcher.UIThread.Post(async () =>
            {
                await InitializeShellOnceAsync(_viewModel).ConfigureAwait(true);
            });
        }

        private void HideForTrayStartup()
        {
            ShowInTaskbar = false;
            Opacity = 0;
            WindowState = WindowState.Minimized;
            Hide();
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_lifecyclePolicy.ResolveCloseAction() == DesktopWindowCloseAction.Close)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_hasStartedShutdown)
            {
                return;
            }

            _hasStartedShutdown = true;
            Closing -= OnClosing;
            Closed -= OnClosed;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.UpdateInstallShutdownRequested -= OnUpdateInstallShutdownRequested;
            _ = ShutdownShellAsync();
        }

        private async Task ShutdownShellAsync()
        {
            try
            {
                await _viewModel.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to shut down the desktop shell.");
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.IsDashboardVisible) && sender is ShellViewModel viewModel)
            {
                ApplyWindowMode(viewModel);
                return;
            }

            if (e.PropertyName == nameof(ShellViewModel.IsSignInStepVisible) && sender is ShellViewModel setupViewModel)
            {
                ApplyWindowMode(setupViewModel);
                return;
            }

            if (e.PropertyName == nameof(ShellViewModel.IsAddSyncPairWizardVisible)
                && sender is ShellViewModel addSyncPairViewModel)
            {
                FocusOverlayAction(addSyncPairViewModel.IsAddSyncPairWizardVisible, CancelAddSyncPairButton);
                return;
            }

            if (e.PropertyName == nameof(ShellViewModel.IsSettingsVisible)
                && sender is ShellViewModel settingsViewModel)
            {
                FocusOverlayAction(settingsViewModel.IsSettingsVisible, CloseSettingsButton);
                return;
            }

            if ((e.PropertyName == nameof(ShellViewModel.IsSelectedSyncPairEditorVisible)
                || e.PropertyName == nameof(ShellViewModel.SelectedSyncPair))
                && sender is ShellViewModel syncPairViewModel)
            {
                ScrollSelectedSyncPairIntoView(syncPairViewModel);
            }
        }

        private static void FocusOverlayAction(bool isVisible, Control action)
        {
            if (!isVisible)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => action.Focus(), DispatcherPriority.Background);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (TryCycleSettingsFocus(e))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && TryCloseActiveOverlay())
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private bool TryCycleSettingsFocus(KeyEventArgs e)
        {
            if (e.Key != Key.Tab
                || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                || !_viewModel.IsSettingsVisible
                || !CloseSettingsButton.IsKeyboardFocusWithin)
            {
                return false;
            }

            if (SettingsTabControl.SelectedItem is Control selectedTab)
            {
                selectedTab.Focus();
            }
            else
            {
                SettingsTabControl.Focus();
            }

            return true;
        }

        private bool TryCloseActiveOverlay()
        {
            if (_viewModel.IsCreateRemoteFolderVisible
                && _viewModel.CancelCreateRemoteFolderCommand.CanExecute(null))
            {
                _viewModel.CancelCreateRemoteFolderCommand.Execute(null);
                return true;
            }

            if (_viewModel.IsAddSyncPairWizardVisible
                && _viewModel.CancelAddSyncPairCommand.CanExecute(null))
            {
                _viewModel.CancelAddSyncPairCommand.Execute(null);
                return true;
            }

            if (_viewModel.IsSettingsVisible
                && _viewModel.CloseSettingsCommand.CanExecute(null))
            {
                _viewModel.CloseSettingsCommand.Execute(null);
                return true;
            }

            return false;
        }

        private void OnUpdateInstallShutdownRequested(object? sender, EventArgs e)
        {
            _lifecyclePolicy.RequestQuit();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            Close();
        }

        private async Task InitializeShellOnceAsync(ShellViewModel viewModel)
        {
            // Avalonia may raise Opened again after Hide()/Show(); tray activation must not reload the shell snapshot.
            if (_hasInitializedShell)
            {
                return;
            }

            _hasInitializedShell = true;
            await viewModel.InitializeAsync().ConfigureAwait(true);
            await viewModel.ApplyVisualSmokeScenarioAsync(_visualSmokeScenario).ConfigureAwait(true);
        }

        private void ScrollSelectedSyncPairIntoView(ShellViewModel viewModel)
        {
            if (!viewModel.IsSelectedSyncPairEditorVisible || viewModel.SelectedSyncPair is null)
            {
                return;
            }

            Guid syncPairId = viewModel.SelectedSyncPair.Id;
            Dispatcher.UIThread.Post(() =>
            {
                Control? row = SyncPairsScrollViewer
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .FirstOrDefault(control => control.Tag is Guid rowSyncPairId && rowSyncPairId == syncPairId);

                if (row is null)
                {
                    return;
                }

                row.BringIntoView();
                Dispatcher.UIThread.Post(
                    () => BringSyncPairRowBottomIntoView(row),
                    DispatcherPriority.Background);
            });
        }

        private static void BringSyncPairRowBottomIntoView(Control row)
        {
            if (row.Bounds.Width <= 0 || row.Bounds.Height <= 0)
            {
                row.BringIntoView();
                return;
            }

            row.BringIntoView(new Rect(0, row.Bounds.Height - 1, row.Bounds.Width, 1));
        }

        private void RemoteFoldersListBox_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not ShellViewModel viewModel
                || !viewModel.OpenRemoteFolderCommand.CanExecute(null))
            {
                return;
            }

            viewModel.OpenRemoteFolderCommand.Execute(null);
        }

        private void SignInInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Key != Key.Enter && e.Key != Key.Return)
                || DataContext is not ShellViewModel viewModel
                || !viewModel.SignInCommand.CanExecute(null))
            {
                return;
            }

            e.Handled = true;
            viewModel.SignInCommand.Execute(null);
        }

        private void ApplyWindowMode(ShellViewModel viewModel)
        {
            WindowProfile profile = ResolveWindowProfile(viewModel);
            if (_windowProfile == profile)
            {
                return;
            }

            _windowProfile = profile;
            MinWidth = profile == WindowProfile.Dashboard ? DashboardMinWidth : SetupMinWidth;
            MinHeight = profile switch
            {
                WindowProfile.Dashboard => DashboardMinHeight,
                WindowProfile.SetupSignIn => SetupSignInMinHeight,
                _ => SetupServerMinHeight,
            };
            Width = profile == WindowProfile.Dashboard ? DashboardWidth : SetupWidth;
            Height = profile switch
            {
                WindowProfile.Dashboard => DashboardHeight,
                WindowProfile.SetupSignIn => SetupSignInHeight,
                _ => SetupServerHeight,
            };
            if (_hasOpened)
            {
                FitAndCenterOnCurrentScreen();
            }
        }

        private static WindowProfile ResolveWindowProfile(ShellViewModel viewModel)
        {
            if (viewModel.IsDashboardVisible)
            {
                return WindowProfile.Dashboard;
            }

            return viewModel.IsSignInStepVisible ? WindowProfile.SetupSignIn : WindowProfile.SetupServer;
        }

        internal static (double Height, double MinHeight) CalculateFittedWindowHeight(
            double desiredHeight,
            double minimumHeight,
            int workingAreaPixelHeight,
            double renderScaling)
        {
            if (desiredHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(desiredHeight));
            }

            if (minimumHeight <= 0 || minimumHeight > desiredHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumHeight));
            }

            if (workingAreaPixelHeight <= 0 || renderScaling <= 0)
            {
                return (desiredHeight, minimumHeight);
            }

            double availableHeight = Math.Max(
                1,
                workingAreaPixelHeight / renderScaling - WindowFrameHeightAllowance);
            return (
                Math.Min(desiredHeight, availableHeight),
                Math.Min(minimumHeight, availableHeight));
        }

        private void FitAndCenterOnCurrentScreen()
        {
            Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
            {
                return;
            }

            double scale = _visualSmokeScale ?? (RenderScaling > 0 ? RenderScaling : screen.Scaling);
            WindowProfile profile = _windowProfile ?? ResolveWindowProfile(_viewModel);
            double desiredHeight = profile switch
            {
                WindowProfile.Dashboard => DashboardHeight,
                WindowProfile.SetupSignIn => SetupSignInHeight,
                _ => SetupServerHeight,
            };
            double desiredMinHeight = profile switch
            {
                WindowProfile.Dashboard => DashboardMinHeight,
                WindowProfile.SetupSignIn => SetupSignInMinHeight,
                _ => SetupServerMinHeight,
            };
            (double fittedHeight, double fittedMinHeight) = CalculateFittedWindowHeight(
                desiredHeight,
                desiredMinHeight,
                screen.WorkingArea.Height,
                scale);
            MinHeight = fittedMinHeight;
            Height = fittedHeight;

            int pixelWidth = (int)Math.Round(Width * scale);
            int pixelHeight = (int)Math.Round(Height * scale);
            PixelRect workingArea = screen.WorkingArea;
            Position = new PixelPoint(
                workingArea.X + Math.Max(0, workingArea.Width - pixelWidth) / 2,
                workingArea.Y + Math.Max(0, workingArea.Height - pixelHeight) / 2);
        }

        private enum WindowProfile
        {
            SetupServer,
            SetupSignIn,
            Dashboard,
        }
    }
}
