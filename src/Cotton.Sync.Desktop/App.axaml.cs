// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop
{
    /// <summary>
    /// Avalonia application entry point.
    /// </summary>
    public partial class App : Application
    {
        private DesktopSingleInstanceActivationServer? _singleInstanceActivationServer;
        private DesktopTrayController? _trayController;
        private CancellationTokenSource? _trayMonitorCancellation;

        internal static DesktopStartupOptions StartupOptions { get; set; } = DesktopStartupOptions.Empty;

        internal static DesktopAppPaths? StartupPaths { get; set; }

        /// <inheritdoc />
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <inheritdoc />
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DesktopAppPaths paths = StartupPaths ?? DesktopAppPaths.CreateDefault();
                DesktopTraceLogging.Install(paths);
                bool useTrayLifecycle = DesktopPlatformCapabilities.IsTrayLifecycleSupported;
                if (useTrayLifecycle)
                {
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                IDesktopShellController controller = StartupOptions.VisualSmokeScenario is { } scenario
                    ? VisualSmokeShellController.Create(scenario)
                    : DesktopShellController.CreateDefault(paths, StartupOptions);
                bool startHiddenToTray =
                    StartupOptions.StartMinimizedToTray
                    && useTrayLifecycle
                    && StartupOptions.VisualSmokeScenario is null;
                MainWindow window = new(
                    controller,
                    StartupOptions.StartMinimizedToTray,
                    useTrayLifecycle,
                    StartupOptions.VisualSmokeScenario,
                    StartupOptions.VisualSmokeScale);
                if (startHiddenToTray)
                {
                    window.StartHiddenToTray();
                }
                else
                {
                    desktop.MainWindow = window;
                    if (StartupOptions.VisualSmokeScenario is not null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            window.Show();
                            window.Activate();
                        });
                    }
                }

                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    _trayController = new DesktopTrayController(this, window, desktop);
                }

                StartTrayHostMonitor(desktop, window);

                _singleInstanceActivationServer = DesktopSingleInstanceActivation.StartServer(
                    paths.SingleInstanceLockPath,
                    () => Dispatcher.UIThread.Post(window.ShowShell));
                desktop.Exit += (_, _) =>
                {
                    _trayMonitorCancellation?.Cancel();
                    _trayController?.Dispose();
                    _singleInstanceActivationServer?.Dispose();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
        private void StartTrayHostMonitor(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }
            _trayMonitorCancellation = new CancellationTokenSource();
            _ = LinuxTrayHostMonitor.RunAsync(available => Dispatcher.UIThread.Post(() =>
            {
                if (_trayMonitorCancellation.IsCancellationRequested)
                {
                    return;
                }

                DesktopPlatformCapabilities.SetLinuxTrayHostAvailability(available);
                _trayController?.SetHostAvailability(available);
                desktop.ShutdownMode = available ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;
                window.SetTrayLifecycleAvailable(available);
            }), _trayMonitorCancellation.Token);
        }
    }
}
