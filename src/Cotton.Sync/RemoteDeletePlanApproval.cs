// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    /// <summary>
    /// Identifies an explicitly approved remote delete plan.
    /// </summary>
    public record RemoteDeletePlanApproval
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteDeletePlanApproval" /> record.
        /// </summary>
        public RemoteDeletePlanApproval(int deleteCount, string planFingerprint)
        {
            if (deleteCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deleteCount), "Approved remote delete count must be positive.");
            }

            if (!RemoteDeletePlanFingerprint.IsValid(planFingerprint))
            {
                throw new ArgumentException(
                    "Remote delete plan fingerprint must be a lowercase SHA-256 value.",
                    nameof(planFingerprint));
            }

            DeleteCount = deleteCount;
            PlanFingerprint = planFingerprint;
        }

        /// <summary>
        /// Gets the approved number of remote deletes.
        /// </summary>
        public int DeleteCount { get; }

        /// <summary>
        /// Gets the SHA-256 fingerprint of the exact approved delete plan.
        /// </summary>
        public string PlanFingerprint { get; }
    }
}
