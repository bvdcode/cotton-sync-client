// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;

namespace Cotton.Sync.Remote
{
    internal static class RemoteNameKey
    {
        public static string Create(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            string normalizedName = name
                .Normalize(NormalizationForm.FormC)
                .Trim()
                .TrimEnd('.');
            if (normalizedName.Length == 0)
            {
                throw new ArgumentException("Remote name must contain a Windows-visible character.", nameof(name));
            }

            return normalizedName.ToUpperInvariant();
        }
    }
}
