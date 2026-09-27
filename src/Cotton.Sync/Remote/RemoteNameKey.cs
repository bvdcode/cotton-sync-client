// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
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

            StringBuilder key = new();
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(normalizedName);
            while (elements.MoveNext())
            {
                string decomposed = elements.GetTextElement().Normalize(NormalizationForm.FormD);
                StringBuilder folded = new();
                foreach (Rune rune in decomposed.EnumerateRunes())
                {
                    UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(rune.Value);
                    if (category is UnicodeCategory.NonSpacingMark
                        or UnicodeCategory.SpacingCombiningMark
                        or UnicodeCategory.EnclosingMark)
                    {
                        continue;
                    }

                    folded.Append(rune.ToString().ToLowerInvariant());
                }

                key.Append(folded.ToString().Normalize(NormalizationForm.FormC));
            }

            return key.ToString();
        }
    }
}
