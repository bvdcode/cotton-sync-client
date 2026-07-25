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

            string normalizedName = name.Normalize(NormalizationForm.FormC).Trim();
            while (normalizedName.EndsWith('.'))
            {
                normalizedName = normalizedName[..^1];
            }

            StringBuilder key = new(normalizedName.Length);
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(normalizedName);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement().Normalize(NormalizationForm.FormD);
                StringBuilder foldedElement = new(element.Length);
                foreach (Rune rune in element.EnumerateRunes())
                {
                    UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(rune.Value);
                    if (category is UnicodeCategory.NonSpacingMark
                        or UnicodeCategory.SpacingCombiningMark
                        or UnicodeCategory.EnclosingMark)
                    {
                        continue;
                    }

                    foldedElement.Append(rune.ToString().ToLowerInvariant());
                }

                key.Append(foldedElement.ToString().Normalize(NormalizationForm.FormC));
            }

            return key.ToString();
        }
    }
}
