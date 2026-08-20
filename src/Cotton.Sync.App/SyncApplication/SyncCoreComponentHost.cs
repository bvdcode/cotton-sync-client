// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Supervision;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.SyncApplication
{
    internal class SyncCoreComponentHost
    {
        private readonly ILocalChangeSyncCoordinator _localChanges;
        private readonly ILogger _logger;
        private readonly IPeriodicSyncCoordinator _periodicSync;
        private readonly IRemoteChangeSyncCoordinator _remoteChanges;
        private readonly IReadOnlyList<ISyncCoreLifecycleComponent> _lifecycleComponents;
        private readonly ISyncSupervisor _supervisor;

        public SyncCoreComponentHost(
            ISyncSupervisor supervisor,
            ILocalChangeSyncCoordinator localChanges,
            IRemoteChangeSyncCoordinator remoteChanges,
            IPeriodicSyncCoordinator periodicSync,
            IReadOnlyList<ISyncCoreLifecycleComponent> lifecycleComponents,
            ILogger logger)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            _localChanges = localChanges ?? throw new ArgumentNullException(nameof(localChanges));
            _remoteChanges = remoteChanges ?? throw new ArgumentNullException(nameof(remoteChanges));
            _periodicSync = periodicSync ?? throw new ArgumentNullException(nameof(periodicSync));
            _lifecycleComponents = lifecycleComponents
                ?? throw new ArgumentNullException(nameof(lifecycleComponents));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(bool isGloballyPaused, CancellationToken cancellationToken)
        {
            List<StartedSyncComponent> startedComponents = [];
            try
            {
                foreach (ISyncCoreLifecycleComponent component in _lifecycleComponents)
                {
                    await component.StartAsync(cancellationToken).ConfigureAwait(false);
                    startedComponents.Add(new StartedSyncComponent(component.Name, component.StopAsync));
                }

                await _supervisor.StartAsync(isGloballyPaused, cancellationToken).ConfigureAwait(false);
                startedComponents.Add(new StartedSyncComponent("sync supervisor", _supervisor.StopAsync));

                await _localChanges.StartAsync(cancellationToken).ConfigureAwait(false);
                startedComponents.Add(new StartedSyncComponent("local change coordinator", _localChanges.StopAsync));

                await _remoteChanges.StartAsync(cancellationToken).ConfigureAwait(false);
                startedComponents.Add(new StartedSyncComponent("remote change coordinator", _remoteChanges.StopAsync));

                await _periodicSync.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollBackAsync(startedComponents).ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _remoteChanges.StopAsync(cancellationToken).ConfigureAwait(false);
            await _periodicSync.StopAsync(cancellationToken).ConfigureAwait(false);
            await _localChanges.StopAsync(cancellationToken).ConfigureAwait(false);
            await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            foreach (ISyncCoreLifecycleComponent component in _lifecycleComponents.Reverse())
            {
                await component.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RollBackAsync(IReadOnlyList<StartedSyncComponent> startedComponents)
        {
            for (int index = startedComponents.Count - 1; index >= 0; index--)
            {
                StartedSyncComponent component = startedComponents[index];
                try
                {
                    await component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Failed to stop {ComponentName} during sync startup rollback.",
                        component.Name);
                }
            }
        }
    }
}
