// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Describes the result of one item in a placeholder batch.
    /// </summary>
    public sealed record RemoteFilePlaceholderBatchResult(
        string RelativePath,
        RemoteFilePlaceholderResult? Placeholder,
        string? UnavailableReason)
    {
        /// <summary>
        /// Gets a value indicating whether the placeholder was created or updated.
        /// </summary>
        public bool IsSuccess => Placeholder is not null;

        /// <summary>
        /// Creates a successful batch result.
        /// </summary>
        public static RemoteFilePlaceholderBatchResult Success(
            RemoteFilePlaceholderRequest request,
            RemoteFilePlaceholderResult placeholder)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(placeholder);
            return new RemoteFilePlaceholderBatchResult(request.RelativePath, placeholder, UnavailableReason: null);
        }

        /// <summary>
        /// Creates an unavailable batch result.
        /// </summary>
        public static RemoteFilePlaceholderBatchResult Unavailable(
            RemoteFilePlaceholderRequest request,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new RemoteFilePlaceholderBatchResult(request.RelativePath, Placeholder: null, reason);
        }
    }
}
