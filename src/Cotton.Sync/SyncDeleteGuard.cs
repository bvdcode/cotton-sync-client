// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal class SyncDeleteGuard
    {
        private readonly int _maximumLocalDeletes;
        private readonly int _maximumRemoteDeletes;
        private readonly RemoteDeletePlanApproval? _approvedRemoteDeletePlan;
        private readonly int _plannedLocalDeletes;
        private readonly int _plannedRemoteDeletes;
        private readonly string _plannedRemoteDeleteFingerprint;

        public SyncDeleteGuard(
            SyncRunOptions options,
            int plannedLocalDeletes,
            IReadOnlyCollection<string> plannedRemoteDeleteItems)
        {
            ArgumentNullException.ThrowIfNull(plannedRemoteDeleteItems);
            _maximumLocalDeletes = options.MaximumLocalDeletesPerRun;
            _maximumRemoteDeletes = options.MaximumRemoteDeletesPerRun;
            _approvedRemoteDeletePlan = options.ApprovedRemoteDeletePlan;
            _plannedLocalDeletes = plannedLocalDeletes;
            _plannedRemoteDeletes = plannedRemoteDeleteItems.Count;
            _plannedRemoteDeleteFingerprint = RemoteDeletePlanFingerprint.Create(plannedRemoteDeleteItems);
        }

        public bool CanDeleteLocal(out string? details)
        {
            return CanDelete(
                _plannedLocalDeletes,
                _maximumLocalDeletes,
                "Local delete blocked by mass-delete guard.",
                out details);
        }

        public bool CanDeleteRemote(out string? details)
        {
            if (_approvedRemoteDeletePlan is not null
                && _approvedRemoteDeletePlan.DeleteCount == _plannedRemoteDeletes
                && string.Equals(
                    _approvedRemoteDeletePlan.PlanFingerprint,
                    _plannedRemoteDeleteFingerprint,
                    StringComparison.Ordinal))
            {
                details = null;
                return true;
            }

            bool canDelete = CanDelete(
                _plannedRemoteDeletes,
                _maximumRemoteDeletes,
                "Remote delete blocked by mass-delete guard.",
                out details);
            if (!canDelete && details is not null)
            {
                details += " Plan fingerprint " + _plannedRemoteDeleteFingerprint + ".";
            }

            return canDelete;
        }

        private static bool CanDelete(
            int planned,
            int maximum,
            string blockedDetails,
            out string? details)
        {
            if (planned > maximum)
            {
                details = blockedDetails + " " + planned + " pending deletes exceed limit " + maximum + ".";
                return false;
            }

            details = null;
            return true;
        }
    }
}
