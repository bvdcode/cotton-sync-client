// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {

        private static ShellViewModel CreateViewModel(
            FakeDesktopShellController controller,
            DesktopFeatureFlags? featureFlags = null,
            FakeLocalFolderPicker? localFolderPicker = null,
            IDesktopNotificationService? notificationService = null,
            IDesktopUiDispatcher? uiDispatcher = null,
            bool checkForUpdatesOnStartup = false,
            bool notifyOnSessionRestore = false,
            TimeSpan? periodicUpdateCheckInterval = null,
            Func<TimeSpan, CancellationToken, Task>? updateDelayAsync = null,
            TimeSpan? storedSessionRetryInterval = null,
            Func<TimeSpan, CancellationToken, Task>? storedSessionRetryDelayAsync = null)
        {
            return new ShellViewModel(
                controller,
                localFolderPicker ?? new FakeLocalFolderPicker(),
                notificationService ?? new FakeDesktopNotificationService(),
                new FakeDesktopThemeService(),
                uiDispatcher ?? new InlineDesktopUiDispatcher(),
                featureFlags,
                checkForUpdatesOnStartup,
                notifyOnSessionRestore,
                periodicUpdateCheckInterval,
                updateDelayAsync,
                storedSessionRetryInterval,
                storedSessionRetryDelayAsync);
        }


        private class ManualPeriodicUpdateDelay : IDisposable
        {
            private readonly Queue<TaskCompletionSource> _pendingDelays = new();
            private bool _disposed;

            public List<TimeSpan> RequestedDelays { get; } = [];

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                RequestedDelays.Add(delay);
                TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    source.SetCanceled(cancellationToken);
                    return source.Task;
                }

                _pendingDelays.Enqueue(source);
                cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
                return source.Task;
            }

            public void ReleaseNextDelay()
            {
                if (_pendingDelays.Count == 0)
                {
                    throw new InvalidOperationException("No periodic update delay is pending.");
                }

                _pendingDelays.Dequeue().SetResult();
            }

            public void Dispose()
            {
                _disposed = true;
                while (_pendingDelays.Count > 0)
                {
                    _pendingDelays.Dequeue().TrySetCanceled();
                }
            }
        }


        private class FakeLocalFolderPicker : ILocalFolderPicker
        {
            private readonly Queue<string?> _selectedPaths;

            public FakeLocalFolderPicker(params string?[] selectedPaths)
            {
                _selectedPaths = new Queue<string?>(selectedPaths);
            }

            public int PickFolderCalls { get; private set; }

            public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PickFolderCalls++;
                return Task.FromResult(_selectedPaths.Count == 0 ? null : _selectedPaths.Dequeue());
            }
        }


        private class FakeDesktopNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => false;

            public void Show(string title, string message)
            {
                throw new NotSupportedException();
            }
        }


        private class CollectingDesktopNotificationService : IDesktopNotificationService
        {
            public bool IsSupported => true;

            public List<(string Title, string Message)> Notifications { get; } = [];

            public void Show(string title, string message)
            {
                Notifications.Add((title, message));
            }
        }


        private class FakeDesktopThemeService : IDesktopThemeService
        {
            public void Apply(AppThemeMode themeMode)
            {
            }
        }


        private class InlineDesktopUiDispatcher : IDesktopUiDispatcher
        {
            public bool CheckAccess()
            {
                return true;
            }

            public void Post(Action action)
            {
                action();
            }

            public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }
        }


        private class QueuedDesktopUiDispatcher : IDesktopUiDispatcher
        {
            private readonly Queue<Action> _actions = [];

            public int PostedActionCount { get; private set; }

            public int PendingActionCount => _actions.Count;

            public bool CheckAccess()
            {
                return false;
            }

            public void Post(Action action)
            {
                PostedActionCount++;
                _actions.Enqueue(action);
            }

            public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }

            public void DrainAll()
            {
                while (_actions.Count > 0)
                {
                    _actions.Dequeue()();
                }
            }
        }


        private class QueuedAccessDesktopUiDispatcher : IDesktopUiDispatcher
        {
            private readonly Queue<Action> _actions = [];

            public int PendingActionCount => _actions.Count;

            public bool CheckAccess()
            {
                return true;
            }

            public void Post(Action action)
            {
                _actions.Enqueue(action);
            }

            public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }

            public void DrainAll()
            {
                while (_actions.Count > 0)
                {
                    _actions.Dequeue()();
                }
            }
        }
    }
}
