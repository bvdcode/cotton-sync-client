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
    /// <summary>
    /// Main desktop shell view model.
    /// </summary>
    internal partial class ShellViewModel : ViewModelBase, IDisposable, IAsyncDisposable
    {
        private const int MaxActivityRows = 30;
        private const int MaxConflictRows = 20;
        private const int MinimumRunProgressEstimateCompletedFiles = 5;
        private const int QueuedWorkIndicatorFileThreshold = 500;
        private const int ServerProbeMaxAttempts = 3;
        private const string QueuedWorkIndicatorText = "Processing queued changes";
        private const string RemoteScanRowProgressLabel = "Checking cloud";
        private const string PreparingCloudFilesProgressLabel = VirtualFileUserFacingCopy.PreparingCloudFilesProgressLabel;
        private const string CreatingCloudFilesProgressLabel = VirtualFileUserFacingCopy.CreatingCloudFilesProgressLabel;
        private static readonly TimeSpan TransferActivityCoalescingWindow = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan VisibleTransferProgressUpdateInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan VisibleRunProgressUpdateInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ServerProbeInitialRetryDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ActiveStatusRunProgressStaleThreshold = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RunTransferMetricsWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MinimumRunTransferSampleDuration = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MinimumRunProgressEstimateDuration = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RunProgressEstimateSmoothingPeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultPeriodicUpdateCheckInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan DefaultStoredSessionRetryInterval = TimeSpan.FromSeconds(15);

        private readonly IDesktopShellController _controller;
        private readonly DesktopFeatureFlags _featureFlags;
        private readonly ILocalFolderPicker _folderPicker;
        private readonly IDesktopNotificationService _notificationService;
        private readonly bool _checkForUpdatesOnStartup;
        private readonly TimeSpan _periodicUpdateCheckInterval;
        private readonly Func<TimeSpan, CancellationToken, Task> _updateDelayAsync;
        private readonly bool _notifyOnSessionRestore;
        private readonly IDesktopThemeService _themeService;
        private readonly IDesktopUiDispatcher _uiDispatcher;
        private readonly StoredSessionRestoreRetryCoordinator _storedSessionRetryCoordinator;
        private readonly object _statusDispatchGate = new();
        private readonly object _activityDispatchGate = new();
        private readonly object _progressDispatchGate = new();
        private readonly DesktopNotificationTracker _notificationTracker = new();
        private readonly Dictionary<Guid, DesktopRunProgressSnapshot> _runProgressByPair = [];
        private readonly Dictionary<Guid, DateTime> _runProgressAppliedAtUtcByPair = [];
        private readonly HashSet<Guid> _suppressedInitialSyncCompleteUntilRunProgressCompleted = [];
        private readonly Dictionary<RunTransferProgressKey, DesktopTransferProgressSnapshot> _transferProgressByKey = [];
        private readonly Dictionary<Guid, long> _runCompletedTransferBytesByPair = [];
        private readonly Dictionary<RunTransferProgressKey, long> _runCompletedTransferBytesByKey = [];
        private readonly Dictionary<RunTransferProgressKey, long> _runTransferBytesByKey = [];
        private readonly Queue<RunFileProgressSample> _runFileProgressSamples = new();
        private readonly Queue<RunTransferProgressSample> _runTransferSamples = new();
        private readonly List<RemoteFolderRowViewModel> _remoteFolderRows = [];
        private readonly SyncPairSettingsValidator _syncPairSettingsValidator = new();
        private readonly Dictionary<Guid, string> _lastStatusErrorActivityMessages = [];
        private string _accountName = "Signed out";
        private string _actionRequiredMessage = string.Empty;
        private long _statusPresentationRevision;
        private string _currentProgressText = "Sign in to start sync.";
        private string _currentRunProgressDetails = string.Empty;
        private string _currentRunProgressTitle = string.Empty;
        private string _currentTransferDetails = string.Empty;
        private string _currentTransferTitle = string.Empty;
        private long _runTransferredBytes;
        private double? _runTransferSpeedBytesPerSecond;
        private double? _currentRunProgressFilesPerSecond;
        private TimeSpan? _runTransferEstimatedTimeRemaining;
        private TimeSpan? _currentRunProgressEstimatedTimeRemaining;
        private DateTime? _lastRunTransferSpeedOccurredAtUtc;
        private DateTime? _lastRunTransferEstimateOccurredAtUtc;
        private DateTime? _lastRunProgressFileRateOccurredAtUtc;
        private DateTime? _lastRunProgressEstimateOccurredAtUtc;
        private string _dataDirectory = string.Empty;
        private string _deviceName = "Cotton Sync Desktop";
        private string _appDatabasePath = string.Empty;
        private string _syncStateDatabasePath = string.Empty;
        private string _tokenStorePath = string.Empty;
        private string _updateStatusText = "Not checked";
        private string _updateDetailsText = "Check GitHub release for updates.";
        private string _downloadedUpdateInstallerPath = string.Empty;
        private bool _isUpdateDownloadProgressVisible;
        private bool _isUpdateDownloadProgressIndeterminate;
        private bool _isUpdateInstallHandoffActive;
        private bool _isUpdateInstallProgressVisible;
        private double _updateDownloadProgressValue;
        private Task? _startupUpdateTask;
        private Task? _periodicUpdateTask;
        private string _globalStatus = "Loading";
        private bool _hasCurrentRunProgress;
        private bool _hasCurrentTransfer;
        private bool _isBusy;
        private bool _isBrowserSignInPending;
        private bool _isStatusDispatchQueued;
        private bool _isCurrentRunProgressIndeterminate;
        private bool _isCurrentTransferIndeterminate;
        private bool _isSignedIn;
        private bool _hasStoredSession;
        private string _storedSessionRestoreMessage = string.Empty;
        private string _lastDiagnosticsBundlePath = string.Empty;
        private string _localFolderPath = string.Empty;
        private string _newRemoteFolderName = string.Empty;
        private string _password = string.Empty;
        private string _remoteFolderFilter = string.Empty;
        private string _browserSignInStatus = string.Empty;
        private string _remoteBrowserPath = "/";
        private string _remoteFolderPath = string.Empty;
        private bool _enableNotifications = true;
        private bool _isApplyingNotificationPreference;
        private bool _isApplyingStartWithOperatingSystem;
        private bool _isApplyingThemePreference;
        private bool _isServerProbeChecking;
        private bool _isServerProbeFailed;
        private bool _isServerVerified;
        private bool _isAddSyncPairWizardVisible;
        private bool _isCreateRemoteFolderVisible;
        private bool _isDesktopSyncChangesApiUnavailable;
        private bool _isLocalFolderSelectionError;
        private bool _isRemoteFolderLoading;
        private bool _isSelectedSyncPairEditorVisible;
        private bool _isSettingsVisible;
        private bool _isActivityVisible;
        private bool _isSyncPausePending;
        private bool _isUpdateAvailable;
        private bool _isUpdateBusy;
        private bool _isUpdateReady;
        private bool _isExportingDiagnostics;
        private bool _isRemovingSyncPair;
        private bool _isAddingSyncPair;
        private bool _isLoadingSnapshot = true;
        private bool _isStartWithOperatingSystemSupported = true;
        private bool _isTrayLifecycleSupported;
        private bool _isWindowsVirtualFilesSupported;
        private SyncPairMode _selectedSyncMode = SyncPairMode.FullMirror;
        private int _selectedSettingsTabIndex;
        private string _trayLifecycleDetails = "Tray lifecycle is not supported on this platform yet.";
        private string _windowsVirtualFilesDetails = "Windows virtual files are not available on this platform.";
        private string _serverUrl = string.Empty;
        private string _serverProbeStatus = string.Empty;
        private bool _startWithOperatingSystem;
        private AppThemeMode _themeMode = AppThemeMode.Dark;
        private double _currentRunProgressValue;
        private double _currentTransferProgressValue;
        private CancellationTokenSource? _serverProbeCancellation;
        private CancellationTokenSource? _browserSignInCancellation;
        private CancellationTokenSource? _startupUpdateCancellation;
        private CancellationTokenSource? _periodicUpdateCancellation;
        private ConflictRowViewModel? _selectedConflict;
        private RemoteFolderRowViewModel? _selectedRemoteFolder;
        private SyncPairRowViewModel? _selectedSyncPair;
        private SyncPairRowViewModel? _pendingRemoveSyncPair;
        private string _totpCode = string.Empty;
        private SyncTransferDirection _transferDirection = SyncTransferDirection.Unknown;
        private Guid? _transferSyncPairId;
        private string _transferRelativePath = string.Empty;
        private string _username = string.Empty;
        private DesktopSyncStatusSnapshot? _pendingStatus;
        private DateTimeOffset? _lastCoalescedActivityAt;
        private Guid? _lastCoalescedActivitySyncPairId;
        private DesktopActivitySnapshot? _pendingCoalescedActivity;
        private bool _isCoalescedActivityDispatchScheduled;
        private DesktopTransferProgressSnapshot? _pendingCoalescedTransferProgress;
        private bool _isCoalescedTransferProgressDispatchScheduled;
        private DesktopRunProgressSnapshot? _pendingCoalescedRunProgress;
        private bool _isCoalescedRunProgressDispatchScheduled;
        private DateTime? _lastVisibleTransferProgressAtUtc;
        private Guid? _visibleTransferSyncPairId;
        private SyncTransferDirection _visibleTransferDirection = SyncTransferDirection.Unknown;
        private string _visibleTransferRelativePath = string.Empty;
        private DateTime? _lastVisibleRunProgressAtUtc;
        private Guid? _visibleRunProgressSyncPairId;
        private SyncRunProgressStage _visibleRunProgressStage = SyncRunProgressStage.Unknown;

        internal ShellViewModel(
            IDesktopShellController controller,
            ILocalFolderPicker folderPicker,
            IDesktopNotificationService notificationService,
            IDesktopThemeService themeService,
            IDesktopUiDispatcher? uiDispatcher = null,
            DesktopFeatureFlags? featureFlags = null,
            bool checkForUpdatesOnStartup = true,
            bool notifyOnSessionRestore = false,
            TimeSpan? periodicUpdateCheckInterval = null,
            Func<TimeSpan, CancellationToken, Task>? updateDelayAsync = null,
            TimeSpan? storedSessionRetryInterval = null,
            Func<TimeSpan, CancellationToken, Task>? storedSessionRetryDelayAsync = null)
        {
            _controller = RequireReference(controller, nameof(controller));
            _featureFlags = ResolveFeatureFlags(featureFlags);
            _folderPicker = RequireReference(folderPicker, nameof(folderPicker));
            _notificationService = RequireReference(notificationService, nameof(notificationService));
            _checkForUpdatesOnStartup = checkForUpdatesOnStartup;
            _periodicUpdateCheckInterval = ResolvePositiveInterval(
                periodicUpdateCheckInterval,
                DefaultPeriodicUpdateCheckInterval,
                nameof(periodicUpdateCheckInterval));
            _updateDelayAsync = ResolveDelay(updateDelayAsync);
            _notifyOnSessionRestore = notifyOnSessionRestore;
            _themeService = RequireReference(themeService, nameof(themeService));
            _uiDispatcher = ResolveUiDispatcher(uiDispatcher);
            _storedSessionRetryCoordinator = new StoredSessionRestoreRetryCoordinator(
                _controller.RestoreStoredSessionAsync,
                _uiDispatcher,
                ResolvePositiveInterval(
                    storedSessionRetryInterval,
                    DefaultStoredSessionRetryInterval,
                    nameof(storedSessionRetryInterval)),
                ResolveDelay(storedSessionRetryDelayAsync),
                ApplyStoredSessionRestoreResult);
            Activities.CollectionChanged += OnActivitiesChanged;
            Conflicts.CollectionChanged += OnConflictsChanged;
            SyncPairs.CollectionChanged += OnSyncPairsChanged;
            RemoteFolders.CollectionChanged += OnRemoteFoldersChanged;
            SelfTestItems.CollectionChanged += OnSelfTestItemsChanged;
            Notifications.CollectionChanged += OnNotificationsChanged;
            _controller.ActivityReported += OnActivityReported;
            _controller.SessionRevoked += OnSessionRevoked;
            _controller.TransferProgressChanged += OnTransferProgressChanged;
            _controller.RunProgressChanged += OnRunProgressChanged;
            _controller.StatusChanged += OnStatusChanged;
            SignInCommand = new AsyncRelayCommand(SignInAsync, CanSignIn, HandleCommandError);
            SignInWithBrowserCommand = new AsyncRelayCommand(
                SignInWithBrowserAsync,
                CanSignInWithBrowser,
                HandleCommandError);
            CancelBrowserSignInCommand = new AsyncRelayCommand(
                CancelBrowserSignInAsync,
                CanCancelBrowserSignIn,
                HandleCommandError);
            RetryStoredSessionCommand = new AsyncRelayCommand(
                RetryStoredSessionAsync,
                CanRetryStoredSession,
                HandleCommandError);
            ChangeServerCommand = new AsyncRelayCommand(ChangeServerAsync, CanChangeServer, HandleCommandError);
            AddSyncPairCommand = new AsyncRelayCommand(AddSyncPairAsync, CanAddSyncPair, HandleCommandError);
            BrowseLocalFolderCommand = new AsyncRelayCommand(BrowseLocalFolderAsync, CanBrowseLocalFolder, HandleCommandError);
            CancelAddSyncPairCommand = new AsyncRelayCommand(
                CancelAddSyncPairAsync,
                CanCancelAddSyncPair,
                HandleCommandError);
            CancelCreateRemoteFolderCommand = new AsyncRelayCommand(
                CancelCreateRemoteFolderAsync,
                CanCancelAddSyncPair,
                HandleCommandError);
            CreateRemoteFolderCommand = new AsyncRelayCommand(CreateRemoteFolderAsync, CanCreateRemoteFolder, HandleCommandError);
            OpenRemoteFolderCommand = new AsyncRelayCommand(OpenRemoteFolderAsync, CanOpenRemoteFolder, HandleCommandError);
            RemoteFolderUpCommand = new AsyncRelayCommand(RemoteFolderUpAsync, CanGoUpRemoteFolder, HandleCommandError);
            ShowCreateRemoteFolderCommand = new AsyncRelayCommand(ShowCreateRemoteFolderAsync, CanShowCreateRemoteFolder, HandleCommandError);
            ShowAddSyncPairCommand = new AsyncRelayCommand(ShowAddSyncPairAsync, CanShowAddSyncPair, HandleCommandError);
            ShowSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => IsSignedIn, HandleCommandError);
            CloseSettingsCommand = new AsyncRelayCommand(CloseSettingsAsync, () => IsSettingsVisible, HandleCommandError);
            SyncNowCommand = new AsyncRelayCommand(SyncNowAsync, () => CanSyncNow, HandleCommandError);
            ApproveRemoteMassDeleteCommand = new AsyncRelayCommand(
                ApproveRemoteMassDeleteAsync,
                () => CanApproveRemoteMassDelete,
                HandleCommandError);
            PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPauseSync, HandleCommandError);
            ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResumeSync, HandleCommandError);
            SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => IsSignedIn, HandleCommandError);
            OpenFolderCommand = new AsyncRelayCommand(
                OpenFolderAsync,
                parameter => ResolveOpenFolderTarget(parameter) is not null,
                HandleCommandError);
            OpenTrayFolderCommand = new AsyncRelayCommand(
                OpenTrayFolderAsync,
                () => CanOpenTrayFolder,
                HandleCommandError);
            OpenConflictCommand = new AsyncRelayCommand(
                OpenConflictAsync,
                CanOpenConflict,
                HandleCommandError);
            ToggleSelectedSyncPairEnabledCommand = new AsyncRelayCommand(
                ToggleSelectedSyncPairEnabledAsync,
                CanEditSelectedSyncPair,
                HandleCommandError);
            SaveSelectedSyncPairNameCommand = new AsyncRelayCommand(
                SaveSelectedSyncPairNameAsync,
                CanEditSelectedSyncPair,
                HandleCommandError);
            RemoveSelectedSyncPairCommand = new AsyncRelayCommand(
                RequestRemoveSelectedSyncPairAsync,
                CanRequestRemoveSelectedSyncPair,
                HandleCommandError);
            ShowSelectedSyncPairEditorCommand = new AsyncRelayCommand(
                ShowSelectedSyncPairEditorAsync,
                CanShowSelectedSyncPairEditor,
                HandleCommandError);
            CancelSelectedSyncPairEditorCommand = new AsyncRelayCommand(
                CancelSelectedSyncPairEditorAsync,
                CanCancelSelectedSyncPairEditor,
                HandleCommandError);
            ConfirmRemoveSelectedSyncPairCommand = new AsyncRelayCommand(
                ConfirmRemoveSelectedSyncPairAsync,
                CanConfirmRemoveSelectedSyncPair,
                HandleCommandError);
            CancelRemoveSyncPairCommand = new AsyncRelayCommand(
                CancelRemoveSyncPairAsync,
                CanCancelRemoveSyncPair,
                HandleCommandError);
            OpenWebCommand = new AsyncRelayCommand(OpenWebAsync, () => IsSignedIn, HandleCommandError);
            ToggleActivityCommand = new AsyncRelayCommand(ToggleActivityAsync, () => IsSignedIn, HandleCommandError);
            SelfTestCommand = new AsyncRelayCommand(SelfTestAsync, () => !IsBusy, HandleCommandError);
            ExportDiagnosticsCommand = new AsyncRelayCommand(
                ExportDiagnosticsAsync,
                () => !IsExportingDiagnostics,
                HandleCommandError);
            CheckForUpdatesCommand = new AsyncRelayCommand(
                CheckForUpdatesAsync,
                () => CanCheckForUpdates,
                HandleCommandError);
            DownloadUpdateCommand = new AsyncRelayCommand(
                DownloadUpdateAsync,
                () => CanDownloadUpdate,
                HandleCommandError);
            InstallUpdateCommand = new AsyncRelayCommand(
                InstallUpdateAsync,
                () => CanInstallUpdate,
                HandleCommandError);
            OpenDataFolderCommand = new AsyncRelayCommand(
                OpenDataFolderAsync,
                CanOpenDataFolder,
                HandleCommandError);
            OpenDiagnosticsBundleFolderCommand = new AsyncRelayCommand(
                OpenDiagnosticsBundleFolderAsync,
                CanOpenDiagnosticsBundleFolder,
                HandleCommandError);
            UseRemoteFolderCommand = new AsyncRelayCommand(UseRemoteFolderAsync, CanUseRemoteFolder, HandleCommandError);
        }
    }
}
